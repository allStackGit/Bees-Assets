"""Project-neutral, measured BF16 training-forward admission for ROCm/CUDA.

Only model forward/backward kernels may use lower precision. The caller
converts actor/critic outputs to float32 before PPO ratios, value clipping,
loss reduction, gradient norm clipping and the float32 optimizer. No
checkpoint state, learner RNG, original parameter, or Adam moment is
modified while measuring the candidate.
"""
from __future__ import annotations

from copy import deepcopy
from statistics import median
from time import perf_counter

import torch
from torch import nn


def _stateful_training_layers(module):
    # Extra qualification forwards must never mutate running statistics or
    # introduce stochastic dropout that defeats a meaningful comparison.
    return any(
        isinstance(layer, (
            nn.modules.dropout._DropoutNd,
            nn.modules.batchnorm._BatchNorm,
        )) or (
            isinstance(layer,(nn.InstanceNorm1d,nn.InstanceNorm2d,
                              nn.InstanceNorm3d)) and layer.track_running_stats
        ) for layer in module.modules())


def _relative_error(reference, candidate):
    reference=reference.detach().float()
    candidate=candidate.detach().float()
    if (not torch.isfinite(reference).all() or
            not torch.isfinite(candidate).all()):
        return float("inf")
    scale=reference.square().sum().sqrt().clamp_min(1e-4)
    return float(((candidate-reference).square().sum().sqrt()/scale).item())


def _compute(models, methods, board, bf16):
    outputs=[]
    parameters=tuple(p for model in models
                     for p in model.parameters() if p.requires_grad)
    with torch.enable_grad():
        with torch.autocast("cuda",dtype=torch.bfloat16,enabled=bf16):
            for model,method in zip(models,methods):
                result=getattr(model,method)(board)
                if not isinstance(result,torch.Tensor):
                    raise ValueError("Training precision requires tensor outputs")
                outputs.append(result.float())
        # The loss and its derivatives remain FP32. BF16 changes only
        # model forward/backward convolution/linear arithmetic.
        objective=sum(output.square().mean() for output in outputs)
        gradients=torch.autograd.grad(
            objective,parameters,allow_unused=True)
    return tuple(output.detach() for output in outputs),tuple(
        grad.detach() if grad is not None else None for grad in gradients)


def qualify_bf16_training_forward(models, methods, *, device, channels,
                                  min_speedup=0.08,
                                  max_parameter_bytes=16*1048576):
    """Qualify a full FP32 vs BF16 learner forward+backward on cloned models.

    Models are cloned only for startup calibration and discarded afterward.
    Each leased GPU is measured independently; the caller enables BF16 only
    when every GPU it may execute on has passed its gradient and throughput
    qualification. Results are pure metadata and never saved in checkpoints.
    """
    result=dict(qualified=0,enabled=0,speedup=0.0)
    if (not isinstance(device,torch.device) or device.type!="cuda" or
            not isinstance(models,(tuple,list)) or not models or
            len(models)!=len(methods) or
            not all(isinstance(model,nn.Module) for model in models) or
            any(not isinstance(method,str) or not method for method in methods) or
            type(channels) is not int or not 1<=channels<=256 or
            not 0<=min_speedup<=0.5):
        return result
    if any(_stateful_training_layers(model) for model in models):
        return result
    total_bytes=sum(p.numel()*p.element_size()
                    for model in models for p in model.parameters())
    if (total_bytes>max_parameter_bytes or
            any(p.dtype!=torch.float32 or p.device.type!="cuda"
                for model in models for p in model.parameters())):
        return result
    if hasattr(torch.cuda,"is_bf16_supported"):
        try:
            with torch.cuda.device(device):
                if not torch.cuda.is_bf16_supported():
                    return result
        except (RuntimeError,NotImplementedError):
            return result
    try:
        clones=tuple(deepcopy(model).to(device) for model in models)
        for clone,original in zip(clones,models):
            clone.train(original.training)
        # No RNG use. The same deterministic input feeds both precisions.
        # A full PPO rollout may mix different shapes, so qualify two
        # representative convolution widths rather than just one map.
        reference_rates=[]
        candidate_rates=[]
        for batch,side in ((8,32),(4,64),(2,47)):
            # Small and large convolution cohorts plus an odd board size
            # capture the relevant MIOpen, instance-norm and padding paths.
            # A bare single-board probe would be dominated by CPU launches,
            # often rejecting useful BF16 training on large PPO groups.
            amount=batch*channels*side*side
            board=torch.arange(amount,device=device,dtype=torch.float32)
            board=((board*0.015625).sin()*0.5).reshape(
                batch,channels,side,side)
            reference,ref_grad=_compute(clones,methods,board,False)
            candidate,trial_grad=_compute(clones,methods,board,True)
            if (any(_relative_error(a,b)>.04
                    for a,b in zip(reference,candidate)) or
                any((a is None)!=(b is None) or
                    (a is not None and _relative_error(a,b)>.12)
                    for a,b in zip(ref_grad,trial_grad))):
                return result

            def duration(bf16):
                torch.cuda.synchronize(device)
                began=perf_counter()
                for _ in range(2):
                    _compute(clones,methods,board,bf16)
                torch.cuda.synchronize(device)
                return (perf_counter()-began)/2

            for attempt in range(4):
                modes=(False,True) if attempt%2==0 else (True,False)
                for bf16 in modes:
                    seconds=duration(bf16)
                    (candidate_rates if bf16 else reference_rates).append(
                        seconds)
        reference=median(reference_rates)
        faster=median(candidate_rates)
        result["qualified"]=1
        if reference>0 and faster<reference*(1-min_speedup):
            result.update(enabled=1,speedup=max(0.,1-faster/reference))
    except (RuntimeError,ValueError,TypeError,NotImplementedError,
            MemoryError,torch.OutOfMemoryError):
        return result
    return result
