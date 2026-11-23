"""GPU-only, project-independent admission for fused Adam/AdamW steps.

Compare actual Adam kernels on independent temporary parameter/gradient/state
storage. No live PPO parameters, optimizer moments, actions, RNG or checkpoint
state are used or changed during qualification. The caller owns the original
optimizer and calls this once before starting its training-update loop.
"""
from __future__ import annotations

from statistics import median
from time import perf_counter

import torch


def qualify_fused_adam(optimizer, *, min_speedup=0.08,
                       max_parameter_bytes=16 * 1048576, samples=5):
    """Select fused Adam only when a representative real-device probe wins.

    Return bounded status fields. Model parameters, Adam moments and exact
    update counters are unchanged by qualification. A restored execution-mode
    flag may be reevaluated for a different machine; either kernel receives
    identical real optimizer state. Both variants use cloned float32 tensors.
    """
    result = dict(gpu_optimizer_fused_adam=0,
                  gpu_optimizer_fused_adam_qualified=0,
                  gpu_optimizer_fused_adam_probe_speedup=0.0)
    family = type(optimizer)
    if (family not in (torch.optim.Adam,torch.optim.AdamW) or
            not 0 <= min_speedup <= 0.5 or
            not 1 <= samples <= 32 or max_parameter_bytes < 1):
        return result
    groups = optimizer.param_groups
    # Execution backend is not checkpoint math. A checkpoint saved by fused
    Adam on one GPU must not force an unqualified fused kernel on another
    CUDA/HIP backend. Return step tensors to ordinary Adam's native CPU
    location before comparing both alternatives on the current device.
    if any(group.get("fused") for group in groups):
        try:
            moved_steps = []
            for group in groups:
                for parameter in group["params"]:
                    entry = optimizer.state.get(parameter)
                    if entry and "step" in entry:
                        step = entry["step"]
                        if step.device.type != "cpu":
                            moved_steps.append((entry,step.to("cpu")))
            for entry,step in moved_steps:
                entry["step"] = step
            for group in groups:
                group["fused"] = None
                group["foreach"] = None
            optimizer.defaults["fused"] = None
            optimizer.defaults["foreach"] = None
            optimizer._step_supports_amp_scaling = False
        except (RuntimeError,ValueError,TypeError,NotImplementedError):
            # Retain the original checkpoint execution mode when even its
            # scalar migration cannot complete; never modify Adam moments.
            result["gpu_optimizer_fused_adam"] = 1
            return result
    params = [p for group in groups for p in group["params"]]
    if (not params or
            any(not isinstance(p,torch.Tensor) or
                p.device.type != "cuda" or p.dtype != torch.float32 or
                not p.is_contiguous() or p.is_sparse or
                p.grad is not None or not p.requires_grad
                for p in params) or
            len({p.device for p in params}) != 1 or
            sum(p.numel()*p.element_size() for p in params) >
            max_parameter_bytes or
            any(group.get("differentiable",False) or
                group.get("capturable",False) or
                isinstance(group.get("lr"),torch.Tensor)
                for group in groups)):
        return result
    device = params[0].device

    def clone_groups(fused):
        prepared = []
        for original in groups:
            copied = []
            for param in original["params"]:
                model = torch.nn.Parameter(param.detach().clone(),
                                           requires_grad=True)
                # Identical deterministic synthetic gradients isolate the
                # optimizer kernel, including Adam's actual state updates.
                model.grad = torch.full_like(model,0.0125)
                copied.append(model)
            settings = {key:value for key,value in original.items()
                        if key not in ("params","fused","foreach","param_names")}
            settings["params"] = copied
            settings["fused"] = True if fused else original.get("fused")
            settings["foreach"] = None if fused else original.get("foreach")
            prepared.append(settings)
        return prepared

    try:
        baseline = family(clone_groups(False))
        candidate = family(clone_groups(True))
        stream = torch.cuda.current_stream(device)
        # First steps allocate native moment tensors and initialize fused
        # optimizers. Neither initialization belongs to steady-state time.
        for _ in range(2):
            baseline.step()
            candidate.step()
        stream.synchronize()
        ordinary_times, fused_times = [], []
        for index in range(samples):
            # Alternate ordering to reduce co-tenant/thermal bias when
            # another independent job shares this physical GPU.
            sequence = ((baseline,ordinary_times),(candidate,fused_times))
            if index % 2:
                sequence = sequence[::-1]
            for engine,measurements in sequence:
                stream.synchronize()
                began = perf_counter()
                engine.step()
                stream.synchronize()
                measurements.append(perf_counter()-began)
        ordinary = median(ordinary_times)
        fused = median(fused_times)
        if ordinary <= 0 or fused >= ordinary*(1-min_speedup):
            return result
        checks = []
        for old_group,new_group in zip(baseline.param_groups,
                                       candidate.param_groups):
            for old,new in zip(old_group["params"],new_group["params"]):
                checks.append(torch.isclose(old,new,rtol=1e-5,
                                            atol=1e-7).all())
                for key in ("exp_avg","exp_avg_sq"):
                    checks.append(torch.isclose(
                        baseline.state[old][key],candidate.state[new][key],
                        rtol=1e-5,atol=1e-7).all())
                if old_group.get("amsgrad"):
                    checks.append(torch.isclose(
                        baseline.state[old]["max_exp_avg_sq"],
                        candidate.state[new]["max_exp_avg_sq"],
                        rtol=1e-5,atol=1e-7).all())
        if not checks or not bool(torch.stack(checks).all().item()):
            return result
        # Ordinary Adam stores its step counters on CPU. Fused Adam's
        # grouped GPU kernel requires those tensors on each parameter's
        # device. Stage every replacement before changing *any* live
        # optimizer state, preserving exact step counts and Adam moments.
        relocated = []
        for group in groups:
            for parameter in group["params"]:
                entry = optimizer.state.get(parameter)
                if entry and "step" in entry:
                    step = entry["step"]
                    if step.device != parameter.device:
                        relocated.append((entry,step.to(parameter.device)))
        for entry,device_step in relocated:
            entry["step"] = device_step
        # Group-level flags are the step dispatch contract. Adam's existing
        # moments/step counters stay intact, including restored checkpoints.
        for group in groups:
            group["foreach"] = None
            group["fused"] = True
        optimizer.defaults["fused"] = True
        optimizer.defaults["foreach"] = None
        optimizer._step_supports_amp_scaling = True
        result.update(gpu_optimizer_fused_adam=1,
                      gpu_optimizer_fused_adam_qualified=1,
                      gpu_optimizer_fused_adam_probe_speedup=
                      max(0.0,1-fused/ordinary))
    except (RuntimeError,ValueError,TypeError,NotImplementedError,
            MemoryError,torch.OutOfMemoryError):
        # Optional fused kernels must never prevent the supported ordinary
        # Adam path from continuing with its original state and hyperparams.
        return result
    return result
