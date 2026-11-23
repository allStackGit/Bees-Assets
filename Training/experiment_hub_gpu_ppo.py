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
        self.row_ranges = {}

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
            # Short action cohorts are launch-bound while larger groups
            # may favor fused arithmetic. Qualify both independently and
            # select only sizes for which GPU forward+backward really wins.
            results=[]
            for rows in sorted(set((min(256,sample_rows),sample_rows))):
                case=(inputs if rows==sample_rows else
                      self._sample(device,dtype,action_count,rows))
                if rows!=sample_rows:
                    small_reference,small_grad=self._forward_backward(
                        ppo_action_terms,case,clip)
                    small_trial,small_trial_grad=self._forward_backward(
                        candidate,case,clip)
                    if (not torch.allclose(
                            small_reference,small_trial,rtol=2e-4,atol=2e-5)
                        or not torch.allclose(
                            small_grad,small_trial_grad,
                            rtol=3e-4,atol=3e-5)):
                        results.append((rows,False,0.))
                        continue
                ordinary_times,compiled_times=[],[]
                for order in range(4):
                    options=((ppo_action_terms,ordinary_times),
                             (candidate,compiled_times))
                    if order%2:
                        options=options[::-1]
                    for fn,times in options:
                        times.append(self._seconds(fn,case,clip,device))
                eager=median(ordinary_times)
                fused=median(compiled_times)
                gain=(1-fused/eager if eager>0 else -1.)
                results.append((rows,gain>=self.min_speedup,gain))
            admitted=[row for row,winning,_ in results if winning]
            if not admitted:
                return False
            if len(admitted)==len(results):
                allowed=(self.min_rows,None)
            elif admitted[0]==max(row for row,_,_ in results):
                # Large cohorts win but small ones don't: keep eager for
                # smaller launch-bound work, including unmeasured middles.
                allowed=(max(admitted),None)
            else:
                # Small cohorts win but big ones don't: don't extrapolate
                # a CUDA graph/codegen win into long ant populations.
                allowed=(self.min_rows,min(admitted))
            self.selected[key]=candidate
            self.row_ranges[key]=allowed
            self.speedups[key]=max(
                gain for _,winning,gain in results if winning)
            return True
        except Exception:
            # Compilation is optional. Leave the numerically established
            # eager loss available on ROCm/CUDA versions with incomplete
            # Triton, AOT-autograd, or dynamic shape support.
            return False

    def __call__(self, logits, actions, old_logp, advantages, clip):
        key=self._key(logits.device,logits.dtype,logits.shape[-1])
        compiled=self.selected.get(key)
        low,high=self.row_ranges.get(key,(self.min_rows,None))
        if (compiled is None or len(logits)<low or
                (high is not None and len(logits)>high) or
                old_logp.dtype!=logits.dtype or
                advantages.dtype!=logits.dtype):
            return ppo_action_terms(
                logits,actions,old_logp,advantages,clip)
        try:
            return compiled(logits,actions,old_logp,advantages,clip)
        except Exception:
            # Disable an unsupported specialization without affecting the
            # live policy or optimizer. No gradients have been accumulated
            # by this pure forward when fallback is selected.
            self.selected[key]=None
            self.row_ranges.pop(key,None)
            return ppo_action_terms(
                logits,actions,old_logp,advantages,clip)

    def metrics(self):
        return {
            "ppo_compiled_action_devices":sum(
                item is not None for item in self.selected.values()),
            "ppo_compiled_action_speedup":(
                max((self.speedups[key] for key,fn in self.selected.items()
                     if fn is not None),default=0.))
        }


def ppo_value_terms(policy, entropy, values, old_values, targets,
                    clip, value_coefficient, entropy_coefficient):
    """Original differentiable PPO value clipping and final loss algebra."""
    clipped = old_values + (values-old_values).clamp(-clip,clip)
    value_loss = torch.maximum(
        (values-targets).square(),(clipped-targets).square())
    loss = (policy+value_coefficient*value_loss
            -entropy_coefficient*entropy)
    return value_loss,loss


class QualifiedPpoValueTerms:
    """Select a faster fused PPO value/total-loss kernel on leased GPUs.

    Qualification executes only synthetic tensor forward+backward graphs
    before training starts. A different optimizer coefficient, dtype, GPU
    or unsupported shape never causes an unqualified live specialization;
    it uses the original eager calculation. Model weights and Adam state
    are never captured or touched.
    """

    def __init__(self, *, min_speedup=.10):
        if not 0<=min_speedup<=.5:
            raise ValueError("Invalid PPO value qualification margin")
        self.min_speedup=min_speedup
        self.selected={}
        self.ranges={}
        self.speedups={}
        self.coefficients={}

    @staticmethod
    def _sample(device,dtype,rows):
        base=torch.linspace(-1.,1.,rows,device=device,dtype=dtype)
        policy=(base*.3).detach().requires_grad_(True)
        entropy=(base*.1+.25).detach().requires_grad_(True)
        value=(base*.67+.11).detach().requires_grad_(True)
        old=(base*.55+.05).detach()
        targets=(base*.8-.06).detach()
        return policy,entropy,value,old,targets

    @staticmethod
    def _forward_backward(fn,inputs,coefficients):
        result=fn(*inputs,*coefficients)
        grads=torch.autograd.grad(result[1].sum(),inputs[:3])
        return tuple(item.detach() for item in result),tuple(
            item.detach() for item in grads)

    @staticmethod
    def _seconds(fn,inputs,coefficients,device):
        torch.cuda.synchronize(device)
        started=perf_counter()
        for _ in range(2):
            QualifiedPpoValueTerms._forward_backward(fn,inputs,coefficients)
        torch.cuda.synchronize(device)
        return (perf_counter()-started)/2

    def qualify(self,device, *, dtype=torch.float32,clip=.2,
                value_coefficient=.5,entropy_coefficient=.001):
        if (not isinstance(device,torch.device) or device.type!='cuda' or
                dtype!=torch.float32 or not hasattr(torch,'compile') or
                not 0<clip<1 or not 0<=value_coefficient<=100 or
                not 0<=entropy_coefficient<=100):
            return False
        key=(str(device),str(dtype))
        coefficients=(float(clip),float(value_coefficient),
                      float(entropy_coefficient))
        if key in self.selected:
            return self.selected[key] is not None
        self.selected[key]=None
        self.coefficients[key]=coefficients
        try:
            candidate=torch.compile(ppo_value_terms,
                                    backend='inductor',
                                    dynamic=True,fullgraph=True)
            results=[]
            for rows in (8,128):
                inputs=self._sample(device,dtype,rows)
                expected,expected_grad=self._forward_backward(
                    ppo_value_terms,inputs,coefficients)
                actual,actual_grad=self._forward_backward(
                    candidate,inputs,coefficients)
                if (any(not torch.allclose(a,b,rtol=2e-4,atol=2e-5)
                        for a,b in zip(expected,actual)) or
                    any(not torch.allclose(a,b,rtol=3e-4,atol=3e-5)
                        for a,b in zip(expected_grad,actual_grad))):
                    results.append((rows,False,0.))
                    continue
                eager_times=[]
                compiled_times=[]
                for round_index in range(4):
                    options=((ppo_value_terms,eager_times),
                             (candidate,compiled_times))
                    if round_index%2:
                        options=options[::-1]
                    for fn,times in options:
                        times.append(self._seconds(
                            fn,inputs,coefficients,device))
                eager=median(eager_times)
                compiled=median(compiled_times)
                gain=(1-compiled/eager if eager>0 else -1.)
                results.append((rows,gain>=self.min_speedup,gain))
            wins=[rows for rows,win,_ in results if win]
            if not wins:
                return False
            if len(wins)==len(results):
                allowed=(1,None)
            elif wins[0]==128:
                allowed=(128,None)
            else:
                allowed=(1,8)
            self.selected[key]=candidate
            self.ranges[key]=allowed
            self.speedups[key]=max(
                gain for _,win,gain in results if win)
            return True
        except Exception:
            return False

    def __call__(self,policy,entropy,values,old_values,targets,
                 clip,value_coefficient,entropy_coefficient):
        key=(str(values.device),str(values.dtype))
        fn=self.selected.get(key)
        low,high=self.ranges.get(key,(1,None))
        if (fn is None or len(values)<low or
                (high is not None and len(values)>high) or
                self.coefficients.get(key)!=(
                    float(clip),float(value_coefficient),float(entropy_coefficient)) or
                any(t.dtype!=values.dtype or t.device!=values.device
                    for t in (policy,entropy,old_values,targets))):
            return ppo_value_terms(policy,entropy,values,old_values,targets,
                                   clip,value_coefficient,entropy_coefficient)
        try:
            return fn(policy,entropy,values,old_values,targets,
                      clip,value_coefficient,entropy_coefficient)
        except Exception:
            self.selected[key]=None
            self.ranges.pop(key,None)
            return ppo_value_terms(policy,entropy,values,old_values,targets,
                                   clip,value_coefficient,entropy_coefficient)

    def metrics(self):
        return {
            'ppo_compiled_value_devices':sum(
                candidate is not None for candidate in self.selected.values()),
            'ppo_compiled_value_speedup':max(
                (self.speedups[key] for key,fn in self.selected.items()
                 if fn is not None),default=0.)}
