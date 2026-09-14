"""Device-portable policy snapshot transport for elastic WAN actors."""

from __future__ import annotations

from typing import Any, Callable, Mapping

import bees_wan_actor_training as wan


def _portable_value(value: Any) -> Any:
    if hasattr(value, "detach") and hasattr(value, "cpu"):
        detached = value.detach().cpu()
        if hasattr(detached, "clone"):
            detached = detached.clone()
        return detached
    return value


def install_portable_policy_transport() -> Callable[[Any], Mapping[str, Any]]:
    original = wan._policy_wire_payload

    def portable(policy: Any) -> Mapping[str, Any]:
        wire = dict(original(policy))
        if wire.get("kind") == "torch":
            weights = wire.get("weights")
            if not isinstance(weights, Mapping):
                raise RuntimeError("Torch WAN policy snapshot is missing its state dictionary")
            wire["weights"] = {
                str(name): _portable_value(value)
                for name, value in weights.items()
            }
        return wire

    wan._policy_wire_payload = portable
    return original


def restore_portable_policy_transport(original: Callable[[Any], Mapping[str, Any]]) -> None:
    wan._policy_wire_payload = original
