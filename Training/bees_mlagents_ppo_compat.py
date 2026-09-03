"""Narrow compatibility fixes for the ML-Agents version used by Bees RL training."""

from __future__ import annotations

from typing import Callable, Optional


VALUE_KEY_PROBE = "__bees_value_key_probe__"


def install_value_estimate_key_fix() -> Optional[Callable[[str], object]]:
    """Fix ML-Agents 1.1.0's value-estimate/return buffer-key collision.

    ML-Agents 1.1.0 defines RewardSignalUtil.value_estimates_key() using the
    RETURNS prefix, so PPO overwrites the old critic predictions with calculated
    returns before the optimizer can use value clipping correctly. The Bees
    launcher already pins/guards ML-Agents 1.1.0; this adds a second structural
    guard so an unexpected vendor change cannot be patched silently.

    Returns the original static method when a patch was installed, or None when
    the installed package already exposes the correct VALUE_ESTIMATES key.
    """

    from mlagents.trainers.buffer import RewardSignalKeyPrefix, RewardSignalUtil

    original = RewardSignalUtil.value_estimates_key
    value_key = original(VALUE_KEY_PROBE)
    returns_key = RewardSignalUtil.returns_key(VALUE_KEY_PROBE)
    correct_key = (RewardSignalKeyPrefix.VALUE_ESTIMATES, VALUE_KEY_PROBE)

    if value_key == correct_key:
        return None

    if value_key != returns_key:
        raise RuntimeError(
            "Unexpected ML-Agents reward-signal key layout: value_estimates_key() "
            f"returned {value_key!r}, returns_key() returned {returns_key!r}. "
            "Refuse to apply the Bees PPO compatibility patch to unknown internals."
        )

    def fixed_value_estimates_key(name: str):
        return RewardSignalKeyPrefix.VALUE_ESTIMATES, name

    RewardSignalUtil.value_estimates_key = staticmethod(fixed_value_estimates_key)

    patched_value_key = RewardSignalUtil.value_estimates_key(VALUE_KEY_PROBE)
    if patched_value_key != correct_key or patched_value_key == returns_key:
        RewardSignalUtil.value_estimates_key = staticmethod(original)
        raise RuntimeError("Failed to separate ML-Agents PPO value-estimate and return keys.")

    return original


def restore_value_estimate_key(original: Optional[Callable[[str], object]]) -> None:
    """Restore the vendor method after the trainer exits."""

    if original is None:
        return

    from mlagents.trainers.buffer import RewardSignalUtil

    RewardSignalUtil.value_estimates_key = staticmethod(original)
