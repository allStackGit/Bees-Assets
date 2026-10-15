"""Opt-in CPU MA-POCA gradient computation, independent of the live learner.

Only Exeter may own Adam or mutate live model weights. These building blocks are
not automatically installed or called by importing the module.
"""

from __future__ import annotations

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


def snapshot_model(optimizer: Any, *, optimizer_revision: int) -> dict[str, Any]:
    """Export the *trainable* actor and critic and their non-gradient state.

    A rollout policy snapshot is insufficient: MA-POCA gradients also depend on
    the centralized critic and its baseline branch.
    """
    def cpu_state(module: Any) -> dict[str, Any]:
        return {
            name: value.detach().to(device="cpu", copy=True).contiguous()
            for name, value in module.state_dict().items()
        }

    if type(optimizer_revision) is not int or optimizer_revision < 0:
        raise ValueError("An explicit nonnegative optimizer revision is required")
    _named_parameters(optimizer)
    return {
        "optimizer_revision": optimizer_revision,
        "actor": cpu_state(optimizer.policy.actor),
        "critic": cpu_state(optimizer.critic),
        "policy_step": int(optimizer.policy.get_current_step()),
    }



def create_cpu_gradient_optimizer(
    behavior_id: str, behavior_spec: Any, run_options: Any, cpu_threads: int
) -> Any:
    """Construct the exact Bees actor and MA-POCA critic on a CPU-only worker."""
    from mlagents.torch_utils import default_device, set_torch_config, torch
    from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
    from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
    from mlagents.trainers.settings import TorchSettings

    from bees_mlagents_structured_policy import install_structured_policy
    from bees_mlagents_ppo_compat import install_value_estimate_key_fix
    from bees_wan_actor_worker import _build_template_policy

    if not 1 <= cpu_threads <= (max(1, int(__import__("os").cpu_count() or 1))):
        raise ValueError("CPU gradient worker thread count exceeds available CPUs")
    set_torch_config(TorchSettings(device="cpu"))
    torch.set_num_threads(cpu_threads)
    if default_device().type != "cpu":
        raise RuntimeError("Refusing to construct a remote gradient optimizer on GPU")
    # Mirror Exeter's patch order: structured actor first, then masked
    # MA-POCA loss and value/return-key compatibility.
    install_structured_policy()
    install_value_estimate_key_fix()
    policy = _build_template_policy(behavior_id, behavior_spec, run_options, 0)
    trainer_settings = run_options.behaviors[
        BehaviorIdentifiers.from_name_behavior_id(behavior_id).brain_name
    ]
    optimizer = TorchPOCAOptimizer(policy, trainer_settings)
    _named_parameters(optimizer)
    return optimizer


def encode_cpu_minibatch(value: Mapping[str, Any]) -> bytes:
    """Torch serialization keeps arbitrary cached group structures intact.

    Loads always map tensors to CPU on the remote machine, even if Exeter's
    fixed input cache is resident on CUDA.
    """
    import io
    from mlagents.torch_utils import torch

    buffer = io.BytesIO()
    torch.save(dict(value), buffer)
    return buffer.getvalue()


def decode_cpu_minibatch(raw: bytes) -> Mapping[str, Any]:
    import io
    from mlagents.torch_utils import torch

    if not isinstance(raw, bytes) or len(raw) > 512 * 1024 * 1024:
        raise ValueError("POCA minibatch serialization exceeds safe payload size")
    value = torch.load(io.BytesIO(raw), map_location="cpu", weights_only=False)
    if not isinstance(value, Mapping):
        raise ValueError("POCA minibatch did not decode to a mapping")
    return value


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
    from bees_mlagents_ppo_compat import (
        _POCA_UPDATE_CACHE_STATE,
    )
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
            "optimizer_revision": int(snapshot["optimizer_revision"]),
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


