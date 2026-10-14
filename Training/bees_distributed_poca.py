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



# The broker and trainer share a process but use separate threads. A single
# active exchange is registered by the running WAN broker. No side effect is
# installed into training merely by importing this module.
_ACTIVE_GRADIENT_EXCHANGE = None
_ACTIVE_GRADIENT_EXCHANGE_LOCK = __import__("threading").Lock()
_SHADOW_PROBE = None
_SHADOW_PROBE_CYCLE = 0


def set_active_gradient_exchange(exchange: Any) -> None:
    global _ACTIVE_GRADIENT_EXCHANGE
    with _ACTIVE_GRADIENT_EXCHANGE_LOCK:
        _ACTIVE_GRADIENT_EXCHANGE = exchange


def _gradient_exchange() -> Any:
    with _ACTIVE_GRADIENT_EXCHANGE_LOCK:
        return _ACTIVE_GRADIENT_EXCHANGE


def poll_shadow_gradient_probe() -> None:
    """Report observed remote compute and compare identical-snapshot gradients.

    This is diagnostic only. The authoritative Adam and PPO update buffers
    are never mutated by shadow gradients.
    """
    global _SHADOW_PROBE
    item = _SHADOW_PROBE
    if not item:
        return
    exchange = item["exchange"]
    result = exchange.wait_result(item["job_id"], 0.0)
    if result is None:
        if time.monotonic() - item["submitted"] >= 600.0:
            exchange.cancel(item["job_id"])
            print("[Bees CPU gradient probe] timed out; no model change", flush=True)
            _SHADOW_PROBE = None
        return
    _SHADOW_PROBE = None
    if "error" in result:
        print("[Bees CPU gradient probe] remote failed: " + str(result["error"]), flush=True)
        return
    from mlagents.torch_utils import torch

    reference = item["local_gradients"]
    remote = result.get("gradients", {})
    if set(remote) != set(reference):
        print("[Bees CPU gradient probe] incompatible remote parameter names", flush=True)
        return
    numerator = 0.0
    remote_norm_sq = 0.0
    local_norm_sq = 0.0
    diff_sq = 0.0
    for name, local in reference.items():
        foreign = remote[name]
        if foreign is None and local is None:
            continue
        if foreign is None or local is None or foreign.shape != local.shape:
            print("[Bees CPU gradient probe] incompatible gradient: " + name, flush=True)
            return
        foreign = foreign.to(device="cpu", dtype=torch.float64)
        local = local.to(device="cpu", dtype=torch.float64)
        numerator += float((foreign * local).sum().item())
        remote_norm_sq += float((foreign * foreign).sum().item())
        local_norm_sq += float((local * local).sum().item())
        diff_sq += float(((foreign - local) ** 2).sum().item())
    cosine = numerator / max(1e-20, math.sqrt(remote_norm_sq * local_norm_sq))
    relative = math.sqrt(diff_sq) / max(1e-10, math.sqrt(local_norm_sq))
    print(
        "[Bees CPU gradient probe] "
        f"batch={result.get('batch_experiences', 0)} "
        f"worker={result.get('worker', '-')} "
        f"remote_compute_s={result.get('compute_seconds', 0):.3f} "
        f"round_trip_s={time.monotonic() - item['submitted']:.3f} "
        f"cosine={cosine:.6f} relative_difference={relative:.6f} "
        "(no remote gradient applied)",
        flush=True,
    )


def begin_shadow_gradient_probe(
    optimizer: Any, minibatch: Mapping[str, Any],
    behavior_id: str, behavior_spec: Any,
) -> str | None:
    """Dispatch at most one shadow minibatch per ten PPO updates."""
    global _SHADOW_PROBE, _SHADOW_PROBE_CYCLE
    import os

    if os.environ.get("BEES_CPU_GRADIENT_PROBE", "") != "1":
        return None
    poll_shadow_gradient_probe()
    if _SHADOW_PROBE is not None:
        return None
    exchange = _gradient_exchange()
    if exchange is None or exchange.metrics()["gradient_workers_active"] < 1:
        return None
    _SHADOW_PROBE_CYCLE += 1
    if _SHADOW_PROBE_CYCLE % 10 != 1:
        return None
    try:
        snapshot = snapshot_model(optimizer)
        serialized = encode_cpu_minibatch(minibatch)
        if len(serialized) > 256 * 1024 * 1024:
            print("[Bees CPU gradient probe] minibatch too large to send", flush=True)
            return None
        job_id = exchange.submit({
            "behavior_id": str(behavior_id),
            "behavior_spec": behavior_spec,
            "snapshot": snapshot,
            "minibatch_bytes": serialized,
        })
        if not job_id:
            return None
        _SHADOW_PROBE = {
            "job_id": job_id,
            "exchange": exchange,
            "submitted": time.monotonic(),
            "local_gradients": None,
        }
        print(
            "[Bees CPU gradient probe] submitted batch=" +
            str(minibatch["loss_masks"].shape[0]) +
            " serialized_mib=" + str(round(len(serialized) / (1024 ** 2), 1)),
            flush=True,
        )
        return job_id
    except Exception as exc:
        print("[Bees CPU gradient probe] dispatch skipped: " +
              f"{type(exc).__name__}: {exc}", flush=True)
        return None


def capture_shadow_reference(optimizer: Any, job_id: str | None) -> None:
    global _SHADOW_PROBE
    if not job_id or _SHADOW_PROBE is None:
        return
    if _SHADOW_PROBE["job_id"] != job_id:
        return
    _SHADOW_PROBE["local_gradients"] = {
        name: None if param.grad is None else
        param.grad.detach().to(device="cpu", copy=True)
        for name, param in _named_parameters(optimizer).items()
    }


def create_cpu_gradient_optimizer(
    behavior_id: str, behavior_spec: Any, run_options: Any, cpu_threads: int
) -> Any:
    """Construct the exact Bees actor and MA-POCA critic on a CPU-only worker."""
    from mlagents.torch_utils import default_device, set_torch_config, torch
    from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
    from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
    from mlagents.trainers.settings import TorchSettings

    from bees_mlagents_ppo_compat import install_value_estimate_key
    from bees_wan_actor_worker import _build_template_policy

    if not 1 <= cpu_threads <= (max(1, int(__import__("os").cpu_count() or 1))):
        raise ValueError("CPU gradient worker thread count exceeds available CPUs")
    set_torch_config(TorchSettings(device="cpu"))
    torch.set_num_threads(cpu_threads)
    if default_device().type != "cpu":
        raise RuntimeError("Refusing to construct a remote gradient optimizer on GPU")
    install_value_estimate_key()
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
