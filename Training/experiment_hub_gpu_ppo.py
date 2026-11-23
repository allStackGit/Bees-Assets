"""Optional project-neutral GPU fusion of PPO's per-action tensor math.

Keeps policy sampling, advantage ownership, grouped reductions, optimizer
state and PPO update scheduling in the calling trainer. Compile and qualify
only synthetic, deterministic tensors before training updates; never compile
inside a live PPO forward or use a real model parameter for qualification.
"""
from __future__ import annotations

from statistics import median
from time import perf_counter

import torch
from torch.nn import functional as F


def ppo_action_terms(logits, actions, old_logp, advantages, clip):
    """One differentiable (policy, entropy, KL, clip fraction) row per action.

    Preserve the original policy clipping, detached diagnostic expressions,
    dtype, and autograd semantics. The caller still reduces across its own
    entity/board ownership, including its optional segmented implementation.
    """
    log_probs = F.log_softmax(logits, dim=-1)
    chosen_logp = log_probs.gather(-1, actions.unsqueeze(-1)).squeeze(-1)
    bounded = log_probs.clamp(min=torch.finfo(log_probs.dtype).min)
    entropy = -(log_probs.exp() * bounded).sum(dim=-1)
    delta = chosen_logp - old_logp
    ratio = delta.exp()
    policy = -torch.minimum(ratio*advantages,
                            ratio.clamp(1-clip, 1+clip)*advantages)
    approximate_kl = ratio.detach()-1-delta.detach()
    clipped_fraction = ((ratio.detach()-1).abs()>clip).float()
    return torch.stack((policy, entropy, approximate_kl, clipped_fraction),dim=1)


class QualifiedPpoActionTerms:
    """Keep only faster, gradient-equivalent compiled ROCm/CUDA PPO kernels.

    The owner explicitly calls qualify() at startup, outside all training
    throughput windows. The object is read-only during multi-GPU forwards,
    so replicated models may safely share it across GPU dispatch threads.
    A failed compilation never changes training state or disables eager PPO.
    """

    def __init__(self, *, min_speedup=.10, min_rows=256):
        if not 0<=min_speedup<=.5 or type(min_rows) is not int or min_rows<1:
            raise ValueError("Invalid PPO GPU compilation qualification")
        self.min_speedup = min_speedup
        self.min_rows = min_rows
        self.selected = {}
        self.speedups = {}

    @staticmethod
    def _key(device, dtype, action_count):
        return (str(device),str(dtype),int(action_count))

    @staticmethod
    def _sample(device, dtype, action_count, rows):
        # Deterministic, non-RNG data and local synthetic autograd graphs.
        base = torch.arange(rows*action_count,
                            device=device,dtype=dtype).reshape(rows,action_count)
        logits = (base*.03125).sin()*.55
        actions = torch.arange(rows,device=device,dtype=torch.int64)%action_count
        old = torch.full((rows,),-1.3,device=device,dtype=dtype)
        advantages = torch.linspace(-1.,1.,rows,device=device,dtype=dtype)
        return logits,actions,old,advantages

    @staticmethod
    def _forward_backward(fn, inputs, clip):
        base,actions,old,advantages = inputs
        logits = base.detach().requires_grad_(True)
        terms = fn(logits,actions,old,advantages,clip)
        # Entropy and the PPO surrogate must both differentiate. KL and
        # clipping diagnostics intentionally have detached ratio/delta.
        scalar = terms[:,0].sum() + .013*terms[:,1].sum()
        gradient, = torch.autograd.grad(scalar,logits)
        return terms.detach(),gradient.detach()

    @staticmethod
    def _seconds(fn, inputs, clip, device):
        torch.cuda.synchronize(device)
        started = perf_counter()
        QualifiedPpoActionTerms._forward_backward(fn,inputs,clip)
        QualifiedPpoActionTerms._forward_backward(fn,inputs,clip)
        torch.cuda.synchronize(device)
        return (perf_counter()-started)/2

    def qualify(self, device, *, dtype=torch.float32, action_count=5,
                sample_rows=2048, clip=.2):
        if not isinstance(device,torch.device) or device.type!="cuda":
            return False
        if (dtype!=torch.float32 or type(action_count) is not int or
                not 2<=action_count<=128 or type(sample_rows) is not int or
                not 256<=sample_rows<=16384 or not 0<clip<1 or
                not hasattr(torch,"compile")):
            return False
        key=self._key(device,dtype,action_count)
        if key in self.selected:
            return self.selected[key] is not None
        self.selected[key]=None
        try:
            inputs=self._sample(device,dtype,action_count,sample_rows)
            candidate=torch.compile(ppo_action_terms,
                                    backend="inductor",
                                    dynamic=True,fullgraph=True)
            reference,reference_grad=self._forward_backward(
                ppo_action_terms,inputs,clip)
            trial,trial_grad=self._forward_backward(candidate,inputs,clip)
            if (not torch.allclose(reference,trial,rtol=2e-4,atol=2e-5) or
                    not torch.allclose(reference_grad,trial_grad,
                                       rtol=3e-4,atol=3e-5)):
                return False
            # Two paired rounds avoid selecting a kernel solely because
            # the ROCm GPU was transiently busy with a separate collector.
            eager_times=[]
            compiled_times=[]
            for order in range(4):
                options=((ppo_action_terms,eager_times),
                         (candidate,compiled_times))
                if order%2:
                    options=options[::-1]
                for fn,times in options:
                    times.append(self._seconds(fn,inputs,clip,device))
            eager=median(eager_times)
            fused=median(compiled_times)
            if eager<=0 or fused>=eager*(1-self.min_speedup):
                return False
            self.selected[key]=candidate
            self.speedups[key]=max(0.,1-fused/eager)
            return True
        except Exception:
            # Compilation is optional. Leave the numerically established
            # eager loss available on ROCm/CUDA versions with incomplete
            # Triton, AOT-autograd, or dynamic shape support.
            return False

    def __call__(self, logits, actions, old_logp, advantages, clip):
        key=self._key(logits.device,logits.dtype,logits.shape[-1])
        compiled=self.selected.get(key)
        if (compiled is None or len(logits)<self.min_rows):
            return ppo_action_terms(
                logits,actions,old_logp,advantages,clip)
        try:
            return compiled(logits,actions,old_logp,advantages,clip)
        except Exception:
            # Disable an unsupported specialization without affecting the
            # live policy or optimizer. No gradients have been accumulated
            # by this pure forward when fallback is selected.
            self.selected[key]=None
            return ppo_action_terms(
                logits,actions,old_logp,advantages,clip)

    def metrics(self):
        return {
            "ppo_compiled_action_devices":sum(
                item is not None for item in self.selected.values()),
            "ppo_compiled_action_speedup":(
                max(self.speedups.values()) if self.speedups else 0.)
        }
