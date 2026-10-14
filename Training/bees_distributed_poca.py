"""CPU worker computations for Bees MA-POCA minibatch gradients.

This module does not own checkpoints or optimizer state. Exeter alone decides
which computed gradients to apply. Nothing is installed into the trainer by
importing this module.
"""

from __future__ import annotations

import math
import time
from collections.abc import Mapping
from typing import Any


def _named_parameters(optimizer: Any) -> dict[str, Any]:
    named = {
        **{"actor." + name: p for name, p in optimizer.policy.actor.named_parameters()},
        **{"critic." + name: p for name, p in optimizer.critic.named_parameters()},
    }
    if len({id(param) for param in named.values()}) != len(named):
        raise RuntimeError("Distributed POCA requires unique actor and critic parameters")
    optimizer_parameters = {
        id(parameter)
        for group in optimizer.optimizer.param_groups
        for parameter in group["params"]
    }
    if {id(param) for param in named.values()} != optimizer_parameters:
        raise RuntimeError("Distributed POCA parameter ownership differs from Adam")
    return named


def snapshot_model(optimizer: Any) -> dict[str, Any]:
    """Export the *trainable* actor and critic and their non-gradient state.

    A rollout policy snapshot is insufficient: MA-POCA gradients also depend on
    the centralized critic and its baseline branch.
    """
    def cpu_state(module: Any) -> dict[str, Any]:
        return {
            name: value.detach().to(device="cpu", copy=True).contiguous()
            for name, value in module.state_dict().items()
        }

    _named_parameters(optimizer)
    return {
        "actor": cpu_state(optimizer.policy.actor),
        "critic": cpu_state(optimizer.critic),
        "policy_step": int(optimizer.policy.get_current_step()),
    }


def compute_minibatch_gradients(
    optimizer: Any,
    minibatch: Mapping[str, Any],
    snapshot: Mapping[str, Any],
) -> dict[str, Any]:
    """Compute real MA-POCA gradients without stepping the remote Adam state.

    The passed optimizer must be a privately constructed CPU-only instance,
    initialized with the same ML-Agents settings, observation/action ABI, and
    Bees runtime patches as Exeter. The minibatch must be a fully materialized
    CPU minibatch produced by the pinned Bees POCA cache path.
    """
    from bees_mlagents_ppo_compat import _POCA_UPDATE_CACHE_STATE
    from mlagents.torch_utils import torch

    if any(p.device.type != "cpu" for p in _named_parameters(optimizer).values()):
        raise ValueError("Remote POCA gradient computation requires CPU parameters")
    if not isinstance(snapshot, Mapping):
        raise ValueError("Remote POCA snapshot must be a mapping")
    if not isinstance(minibatch, Mapping) or not minibatch:
        raise ValueError("Remote POCA minibatch must be a non-empty mapping")
    optimizer.policy.actor.load_state_dict(snapshot["actor"], strict=True)
    optimizer.critic.load_state_dict(snapshot["critic"], strict=True)
    optimizer.policy.set_step(int(snapshot["policy_step"]))
    batch_size = int(minibatch["loss_masks"].shape[0])
    if batch_size < 1:
        raise ValueError("Remote POCA minibatch contains no experiences")

    # The normal optimizer.update() executes exactly the existing Bees
    # masked-action/weighted-advantage/centralized-critic loss and backward
    # methods. Only Adam.step is suppressed; the remote worker is NOT an
    # independent optimizer and must not modify its authoritative parameters.
    step = optimizer.optimizer.step
    if getattr(_POCA_UPDATE_CACHE_STATE, "minibatch", None) is not None:
        raise RuntimeError("Remote POCA optimizer already has an active minibatch")
    optimizer.optimizer.step = lambda *args, **kwargs: None
    _POCA_UPDATE_CACHE_STATE.minibatch = minibatch
    _POCA_UPDATE_CACHE_STATE.actor_update = True
    started = time.monotonic()
    try:
        stats = optimizer.update(minibatch, batch_size)
        grads: dict[str, Any] = {}
        for name, param in _named_parameters(optimizer).items():
            grad = param.grad
            if grad is not None:
                if not bool(torch.isfinite(grad).all().item()):
                    raise FloatingPointError("Non-finite distributed POCA gradient: " + name)
                grads[name] = grad.detach().to(device="cpu", copy=True).contiguous()
            else:
                grads[name] = None
        return {
            "policy_step": int(snapshot["policy_step"]),
            "batch_experiences": batch_size,
            "compute_seconds": time.monotonic() - started,
            "gradients": grads,
            "stats": {
                name: float(value.detach().cpu().item()) if hasattr(value, "detach")
                else float(value)
                for name, value in stats.items()
            },
        }
    finally:
        _POCA_UPDATE_CACHE_STATE.minibatch = None
        _POCA_UPDATE_CACHE_STATE.actor_update = True
        optimizer.optimizer.step = step


def apply_minibatch_gradients(
    optimizer: Any,
    result: Mapping[str, Any],
    *,
    expected_policy_step: int | None = None,
) -> int:
    """Apply validated remote gradients using Exeter's existing Adam state.

    The coordinator must separately verify session/job identity and reject
    duplicates before calling this method; this function never trusts workers
    with optimizer state or model updates.
    """
    from mlagents.torch_utils import torch

    if not isinstance(result, Mapping) or not isinstance(result.get("gradients"), Mapping):
        raise ValueError("Malformed distributed POCA gradient result")
    if (
        expected_policy_step is not None and
        int(result.get("policy_step", -1)) != expected_policy_step
    ):
        raise ValueError("Distributed POCA policy step mismatch")
    named = _named_parameters(optimizer)
    grads = result["gradients"]
    if set(grads) != set(named):
        raise ValueError("Distributed POCA gradient keys differ from authoritative model")

    # Validate EVERYTHING before touching the live gradients or Adam state.
    for name, param in named.items():
        grad = grads[name]
        if grad is None:
            continue
        if (
            not isinstance(grad, torch.Tensor)
            or grad.device.type != "cpu"
            or grad.shape != param.shape
            or grad.dtype != param.dtype
            or not bool(torch.isfinite(grad).all().item())
        ):
            raise ValueError("Invalid distributed POCA gradient: " + name)
    if not isinstance(result.get("batch_experiences"), int) or result["batch_experiences"] < 1:
        raise ValueError("Invalid distributed POCA gradient batch size")

    optimizer.optimizer.zero_grad(set_to_none=True)
    for name, param in named.items():
        grad = grads[name]
        if grad is not None:
            param.grad = grad.to(device=param.device, non_blocking=False)
    # Preserve the authoritative optimizer's Adam moments and step counter.
    # CUDA-graph modes remain Exeter-owned; this method is for eager Adam.
    optimizer.optimizer.step()
    return int(result["batch_experiences"])
