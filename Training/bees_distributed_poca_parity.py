"""Opt-in CPU/GPU parity calculations for the first shadow PPO minibatch.

No training state is modified; capture is done on the optimizer thread after
backward and the reference is compared on the detached background thread.
"""

from __future__ import annotations

import math
import threading
from typing import Any

from bees_distributed_poca import _named_parameters


class LocalGradientReference:
    def __init__(self) -> None:
        self.ready = threading.Event()
        self.gradients: dict | None = None
        self.stats: dict | None = None
        self.error: str | None = None

    def capture(self, optimizer: Any, stats: dict) -> None:
        try:
            self.gradients = {
                name: None if parameter.grad is None
                else parameter.grad.detach().to(device="cpu", copy=True)
                for name, parameter in _named_parameters(optimizer).items()
            }
            self.stats = {
                name: float(v.detach().cpu().item()) if hasattr(v, "detach")
                else float(v)
                for name, v in stats.items()
            }
        except Exception as exc:
            self.error = f"{type(exc).__name__}: {exc}"
        finally:
            self.ready.set()

    def compare(self, remote: dict, timeout: float = 30.0) -> str:
        from mlagents.torch_utils import torch

        if not self.ready.wait(timeout):
            return "parity=local_reference_timeout"
        if self.error:
            return "parity=local_reference_error:" + self.error
        a, b = self.gradients, remote.get("gradients")
        if not isinstance(a, dict) or not isinstance(b, dict) or set(a) != set(b):
            return "parity=incompatible_parameter_names"
        families = {"actor": [0.0, 0.0, 0.0, 0.0],
                    "critic": [0.0, 0.0, 0.0, 0.0]}
        worst = (0.0, "-")
        disagreements = []
        for name, left in a.items():
            right = b[name]
            if left is None and right is None:
                continue
            if (left is None or not isinstance(right, torch.Tensor)
                    or left.shape != right.shape
                    or not bool(torch.isfinite(left).all())
                    or not bool(torch.isfinite(right).all())):
                return "parity=invalid_gradient:" + name
            family = name.split(".", 1)[0]
            if family not in families:
                return "parity=unexpected_parameter:" + name
            lhs = left.to(device="cpu", dtype=torch.float64)
            rhs = right.to(device="cpu", dtype=torch.float64)
            values = (
                float((lhs * rhs).sum()),
                float((lhs * lhs).sum()),
                float((rhs * rhs).sum()),
                float(((lhs - rhs) ** 2).sum()),
            )
            for i, value in enumerate(values):
                families[family][i] += value
            if values[3] > worst[0]:
                worst = (values[3], name)
            if values[3] > 0.0:
                disagreements.append((values[3], name, values[1]))
        parts = []
        for family, (inner, local_sq, remote_sq, diff_sq) in families.items():
            cosine = inner / max(1e-20, math.sqrt(local_sq * remote_sq))
            relative = math.sqrt(diff_sq) / max(1e-10, math.sqrt(local_sq))
            parts.extend((f"{family}_cosine={cosine:.6f}",
                          f"{family}_relative={relative:.6f}",
                          f"{family}_local_grad_norm={math.sqrt(local_sq):.6g}"))
        parts.append("worst_param=" + worst[1])
        for index, (difference_sq, name, local_sq) in enumerate(
            sorted(disagreements, reverse=True)[:3], start=1
        ):
            absolute = math.sqrt(difference_sq)
            relative = absolute / max(1e-10, math.sqrt(local_sq))
            parts.extend((
                f"disagreement_{index}_param={name}",
                f"disagreement_{index}_abs_l2={absolute:.6g}",
                f"disagreement_{index}_relative_l2={relative:.6g}",
            ))
        old_stats, new_stats = self.stats or {}, remote.get("stats") or {}
        for name in ("Losses/Policy Loss", "Losses/Value Loss",
                     "Losses/Baseline Loss"):
            if name in old_stats and name in new_stats:
                tag = name.lower().replace("/", "_").replace(" ", "_")
                parts.append(
                    f"{tag}_abs_diff={abs(old_stats[name] - float(new_stats[name])):.6g}"
                )
        return " ".join(parts)
