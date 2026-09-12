"""Narrow compatibility fixes and exploration guardrails for Bees PPO training."""

from __future__ import annotations

import math
from typing import Callable, Optional


VALUE_KEY_PROBE = "__bees_value_key_probe__"
MAX_CONTINUOUS_SIGMA = 1.5
_ORIGINAL_GAUSSIAN_FORWARD = None


def install_continuous_sigma_guard(
    max_sigma: float = MAX_CONTINUOUS_SIGMA,
) -> Optional[Callable]:
    """Cap continuous-action exploration variance without changing the policy ABI.

    ML-Agents 1.1.0 learns one unconstrained ``log_sigma`` parameter per
    continuous action when ``conditional_sigma`` is disabled. Bees uses that
    default layout. Large values can make almost every sample hit ML-Agents'
    downstream action clamp, reducing movement/aiming to saturated directions.

    The guard clamps the learned unconditional parameter before every forward
    pass. Conditional distributions, if introduced later, have their emitted
    standard deviation clamped instead. The lower side is intentionally left
    unconstrained so the policy can still become precise; Bees keeps a nonzero
    PPO beta as the exploration floor.
    """

    if not math.isfinite(max_sigma) or max_sigma <= 0.0:
        raise ValueError(f"max_sigma must be finite and positive; got {max_sigma!r}.")

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.distributions import GaussianDistribution

    global _ORIGINAL_GAUSSIAN_FORWARD
    if _ORIGINAL_GAUSSIAN_FORWARD is not None:
        return None

    original_forward = GaussianDistribution.forward
    max_log_sigma = math.log(max_sigma)

    def guarded_forward(self, inputs):
        if self.conditional_sigma:
            distribution = original_forward(self, inputs)
            distribution.std = torch.clamp(distribution.std, max=max_sigma)
            return distribution

        # Clamp the parameter itself so resumed checkpoints with pathological
        # sigma values are repaired before they can affect an action, loss, or
        # ONNX export. no_grad keeps the projection outside the PPO gradient.
        with torch.no_grad():
            self.log_sigma.clamp_(max=max_log_sigma)
        return original_forward(self, inputs)

    GaussianDistribution.forward = guarded_forward
    _ORIGINAL_GAUSSIAN_FORWARD = original_forward
    return original_forward


def restore_continuous_sigma_guard(original: Optional[Callable] = None) -> None:
    """Restore ML-Agents' Gaussian forward method after the trainer exits."""

    global _ORIGINAL_GAUSSIAN_FORWARD
    installed_original = _ORIGINAL_GAUSSIAN_FORWARD
    if installed_original is None:
        return

    from mlagents.trainers.torch_entities.distributions import GaussianDistribution

    GaussianDistribution.forward = original or installed_original
    _ORIGINAL_GAUSSIAN_FORWARD = None


def install_value_estimate_key_fix() -> Optional[Callable[[str], object]]:
    """Install Bees' ML-Agents 1.1.0 PPO compatibility fixes.

    ML-Agents 1.1.0 defines RewardSignalUtil.value_estimates_key() using the
    RETURNS prefix, so PPO overwrites the old critic predictions with calculated
    returns before the optimizer can use value clipping correctly. The Bees
    launcher already pins/guards ML-Agents 1.1.0; this adds a second structural
    guard so an unexpected vendor change cannot be patched silently.

    This installer also enables the Bees continuous-sigma guard. Keeping both
    fixes behind the launcher's existing compatibility hook avoids changing the
    policy ABI or requiring a fork of ML-Agents.

    Returns the original static method when the value-key patch was installed,
    or None when the installed package already exposes the correct key.
    """

    from mlagents.trainers.buffer import RewardSignalKeyPrefix, RewardSignalUtil

    original = RewardSignalUtil.value_estimates_key
    value_key = original(VALUE_KEY_PROBE)
    returns_key = RewardSignalUtil.returns_key(VALUE_KEY_PROBE)
    correct_key = (RewardSignalKeyPrefix.VALUE_ESTIMATES, VALUE_KEY_PROBE)

    if value_key == correct_key:
        install_continuous_sigma_guard()
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

    try:
        install_continuous_sigma_guard()
    except Exception:
        RewardSignalUtil.value_estimates_key = staticmethod(original)
        raise

    return original


def restore_value_estimate_key(original: Optional[Callable[[str], object]]) -> None:
    """Restore vendor PPO methods after the trainer exits."""

    if original is not None:
        from mlagents.trainers.buffer import RewardSignalUtil

        RewardSignalUtil.value_estimates_key = staticmethod(original)

    restore_continuous_sigma_guard()
