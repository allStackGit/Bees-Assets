"""CPU worker computations for Bees MA-POCA minibatch gradients.

This module does not own checkpoints or optimizer state. Exeter alone decides
which computed gradients to apply. Nothing is installed into the trainer by
importing this module.
"""

from __future__ import annotations

import math
import threading
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


def poll_shadow_gradient_probe(result: Mapping[str, Any]) -> None:
    """Report an asynchronously collected remote CPU gradient result.

    This is diagnostic only. The authoritative Adam and PPO update buffers
    are never mutated by shadow gradients.
    """
    global _SHADOW_PROBE
    item = _SHADOW_PROBE
    if not item:
        return
    exchange = item["exchange"]
    if _SHADOW_PROBE is item:
        _SHADOW_PROBE = None
    exchange.forget_snapshot(item["snapshot_id"])
    if "error" in result:
        print("[Bees CPU gradient probe] remote failed: " + str(result["error"]), flush=True)
        return
    from mlagents.torch_utils import torch

    reference = item["local_gradients"]
    remote = result.get("gradients", {})
    if not isinstance(reference, dict):
        print("[Bees CPU gradient probe] local reference unavailable; no model change", flush=True)
        return
    if not isinstance(remote, Mapping) or set(remote) != set(reference):
        print("[Bees CPU gradient probe] incompatible remote parameter names", flush=True)
        return
    totals = {
        "all": [0.0, 0.0, 0.0, 0.0],
        "actor": [0.0, 0.0, 0.0, 0.0],
        "critic": [0.0, 0.0, 0.0, 0.0],
    }
    per_parameter = []
    for name, local in reference.items():
        foreign = remote[name]
        if foreign is None and local is None:
            continue
        if (
            foreign is None or local is None
            or not isinstance(foreign, torch.Tensor)
            or not isinstance(local, torch.Tensor)
            or foreign.shape != local.shape
            or not bool(torch.isfinite(foreign).all().item())
            or not bool(torch.isfinite(local).all().item())
        ):
            print("[Bees CPU gradient probe] incompatible/nonfinite gradient: " + name, flush=True)
            return
        foreign = foreign.to(device="cpu", dtype=torch.float64)
        local = local.to(device="cpu", dtype=torch.float64)
        values = (
            float((foreign * local).sum().item()),
            float((foreign * foreign).sum().item()),
            float((local * local).sum().item()),
            float(((foreign - local) ** 2).sum().item()),
        )
        family = name.split(".", 1)[0]
        if family not in totals:
            print("[Bees CPU gradient probe] unknown gradient family: " + name, flush=True)
            return
        for key in ("all", family):
            for index, value in enumerate(values):
                totals[key][index] += value
        per_parameter.append((name, values[3]))

    def metrics(family):
        product, foreign_sq, local_sq, difference_sq = totals[family]
        cosine = product / max(1e-20, math.sqrt(foreign_sq * local_sq))
        relative = math.sqrt(difference_sq) / max(1e-10, math.sqrt(local_sq))
        return cosine, relative, math.sqrt(local_sq), math.sqrt(foreign_sq)

    cosine, relative, local_norm, remote_norm = metrics("all")
    actor_cosine, actor_relative, _, _ = metrics("actor")
    critic_cosine, critic_relative, _, _ = metrics("critic")
    timing = result.get("_broker_timings", {})
    if not isinstance(timing, Mapping):
        timing = {}
    print(
        "[Bees CPU gradient probe] "
        f"batch={result.get('batch_experiences', 0)} "
        f"worker={result.get('worker', '-')} "
        f"remote_compute_s={result.get('compute_seconds', 0):.3f} "
        f"snapshot_fetch_s={float(result.get('snapshot_fetch_seconds', 0)):.3f} "
        f"snapshot_cache_hit={int(bool(result.get('snapshot_cache_hit', False)))} "
        f"round_trip_s={time.monotonic() - item['submitted']:.3f} "
        f"queue_s={float(timing.get('queue_seconds', 0)):.2f} "
        f"in_flight_s={float(timing.get('in_flight_seconds', 0)):.2f} "
        f"poll_delay_s={float(timing.get('poll_delay_seconds', 0)):.2f} "
        f"cosine={cosine:.6f} relative_difference={relative:.6f} "
        f"actor_cosine={actor_cosine:.6f} actor_relative={actor_relative:.6f} "
        f"critic_cosine={critic_cosine:.6f} critic_relative={critic_relative:.6f} "
        "(no remote gradient applied)",
        flush=True,
    )
    print(
        "[Bees CPU gradient detail] "
        f"local_norm={local_norm:.6g} remote_norm={remote_norm:.6g} "
        "largest_differences=" + ",".join(
            f"{name}:{math.sqrt(squared) / max(1e-10, local_norm):.5f}"
            for name, squared in sorted(
                per_parameter, key=lambda item: item[1], reverse=True
            )[:5]
        ),
        flush=True,
    )
    local_trace = item.get("local_trace")
    remote_trace = result.get("policy_trace")
    if isinstance(local_trace, Mapping) and isinstance(remote_trace, Mapping):
        try:
            local_active = local_trace["active"]
            remote_active = remote_trace["active"]
            if local_active.shape != remote_active.shape:
                raise ValueError("different policy action shapes")
            if not bool((local_active == remote_active).all().item()):
                raise ValueError("different active action dimensions")
            active = local_active
            local_prob = local_trace["log_probs"]
            remote_prob = remote_trace["log_probs"]
            if local_prob.shape != remote_prob.shape:
                raise ValueError("different policy log-probability shapes")
            delta = (local_prob - remote_prob).abs()
            active_delta = delta[active]
            if active_delta.numel() == 0:
                raise ValueError("no active actions to compare")
            clipping_disagreement = int(
                ((local_trace["saturated"] != remote_trace["saturated"]) & active).sum().item()
            )
            print(
                "[Bees CPU gradient clipping] "
                f"active={int(active.sum().item())} "
                f"cpu_near_1e4={int(remote_trace['near_1e4'])} "
                f"exeter_near_1e4={int(local_trace['near_1e4'])} "
                f"cpu_near_1e3={int(remote_trace['near_1e3'])} "
                f"exeter_near_1e3={int(local_trace['near_1e3'])} "
                f"clipping_disagreement={clipping_disagreement} "
                f"logprob_max_abs={float(active_delta.max().item()):.8g} "
                f"logprob_mean_abs={float(active_delta.mean().item()):.8g}",
                flush=True,
            )
        except (KeyError, ValueError, RuntimeError, TypeError) as exc:
            print(
                "[Bees CPU gradient clipping] comparison unavailable: "
                + str(exc), flush=True,
            )
    local_stats = item.get("local_stats")
    remote_stats = result.get("stats")
    if isinstance(local_stats, Mapping) and isinstance(remote_stats, Mapping):
        shared = sorted(set(local_stats).intersection(remote_stats))
        if shared:
            print(
                "[Bees CPU gradient losses] " + " ".join(
                    f"{name.replace(' ', '_')}="
                    f"{float(local_stats[name]):.6g}/{float(remote_stats[name]):.6g}"
                    for name in shared
                ) + " (Exeter/CPU)",
                flush=True,
            )



def _await_shadow_gradient_probe(item: Mapping[str, Any]) -> None:
    """Await the submitted work off the PPO thread; never touch live model state."""
    global _SHADOW_PROBE
    exchange = item["exchange"]
    try:
        result = exchange.wait_result(item["job_id"], 600.0)
        if result is None:
            exchange.cancel(item["job_id"])
            exchange.forget_snapshot(item["snapshot_id"])
            if _SHADOW_PROBE is item:
                _SHADOW_PROBE = None
            print("[Bees CPU gradient probe] timed out; no model change", flush=True)
            return
        poll_shadow_gradient_probe(result)
    except Exception as exc:
        exchange.cancel(item["job_id"])
        exchange.forget_snapshot(item["snapshot_id"])
        if _SHADOW_PROBE is item:
            _SHADOW_PROBE = None
        print(
            "[Bees CPU gradient probe] comparison failed: "
            f"{type(exc).__name__}: {exc} (no model change)",
            flush=True,
        )


def begin_shadow_gradient_probe(
    optimizer: Any, minibatch: Mapping[str, Any],
    behavior_id: str, behavior_spec: Any,
) -> str | None:
    """Dispatch one shadow minibatch per PPO update while the probe is enabled."""
    global _SHADOW_PROBE, _SHADOW_PROBE_CYCLE
    import os

    if os.environ.get("BEES_CPU_GRADIENT_PROBE", "") != "1":
        return None
    if _SHADOW_PROBE is not None:
        return None
    exchange = _gradient_exchange()
    if exchange is None or exchange.metrics()["gradient_workers_active"] < 1:
        return None
    _SHADOW_PROBE_CYCLE += 1
    try:
        snapshot = snapshot_model(optimizer)
        serialized = encode_cpu_minibatch(minibatch)
        if len(serialized) > 256 * 1024 * 1024:
            print("[Bees CPU gradient probe] minibatch too large to send", flush=True)
            return None
        # The broker retains one encoded immutable snapshot; jobs transmit
        # only its identity. The CPU worker fetches it once and caches it while
        # the session is valid. The same token may serve multiple minibatches
        # computed against one frozen parameter version.
        from bees_wan_actor_training import encode_payload
        snapshot_id = exchange.publish_snapshot(encode_payload(snapshot))
        if not snapshot_id:
            return None
        try:
            job_id = exchange.submit({
                "behavior_id": str(behavior_id),
                "behavior_spec": behavior_spec,
                "snapshot_id": snapshot_id,
                "minibatch_bytes": serialized,
            })
        except Exception:
            exchange.forget_snapshot(snapshot_id)
            raise
        if not job_id:
            exchange.forget_snapshot(snapshot_id)
            return None
        _SHADOW_PROBE = {
            "snapshot_id": snapshot_id,
            "job_id": job_id,
            "exchange": exchange,
            "submitted": time.monotonic(),
            "local_gradients": None,
        }
        from bees_mlagents_ppo_compat import _POCA_GRADIENT_PROBE_STATE
        _POCA_GRADIENT_PROBE_STATE.trace = None
        _POCA_GRADIENT_PROBE_STATE.enabled = True
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


def capture_shadow_reference(
    optimizer: Any, job_id: str | None, update_stats: Mapping[str, Any] | None = None
) -> None:
    global _SHADOW_PROBE
    if not job_id or _SHADOW_PROBE is None:
        return
    if _SHADOW_PROBE["job_id"] != job_id:
        return
    from bees_mlagents_ppo_compat import _POCA_GRADIENT_PROBE_STATE
    _SHADOW_PROBE["local_trace"] = getattr(_POCA_GRADIENT_PROBE_STATE, "trace", None)
    _POCA_GRADIENT_PROBE_STATE.enabled = False
    _POCA_GRADIENT_PROBE_STATE.trace = None
    _SHADOW_PROBE["local_gradients"] = {
        name: None if param.grad is None else
        param.grad.detach().to(device="cpu", copy=True)
        for name, param in _named_parameters(optimizer).items()
    }
    if update_stats is not None:
        _SHADOW_PROBE["local_stats"] = {
            name: (
                float(value.detach().cpu().item())
                if hasattr(value, "detach") else float(value)
            )
            for name, value in update_stats.items()
        }
    threading.Thread(
        target=_await_shadow_gradient_probe,
        args=(_SHADOW_PROBE,),
        name="bees-cpu-gradient-probe",
        daemon=True,
    ).start()


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
        _POCA_GRADIENT_PROBE_STATE,
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
    _POCA_GRADIENT_PROBE_STATE.enabled = True
    _POCA_GRADIENT_PROBE_STATE.trace = None
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
            "policy_trace": getattr(_POCA_GRADIENT_PROBE_STATE, "trace", None),
            "stats": {
                name: float(value.detach().cpu().item()) if hasattr(value, "detach")
                else float(value)
                for name, value in stats.items()
            },
        }
    finally:
        _POCA_GRADIENT_PROBE_STATE.enabled = False
        _POCA_GRADIENT_PROBE_STATE.trace = None
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
