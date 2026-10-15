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
        local, received = self.gradients, remote.get("gradients")
        if not isinstance(local, dict) or not isinstance(received, dict):
            return "parity=invalid_gradient_mapping"
        if set(local) != set(received):
            only_local = sorted(set(local) - set(received))
            only_remote = sorted(set(received) - set(local))
            return (
                "parity=incompatible_parameter_names "
                f"only_local={only_local[:3]} only_remote={only_remote[:3]}"
            )

        families = {"actor": [0.0, 0.0, 0.0, 0.0],
                    "critic": [0.0, 0.0, 0.0, 0.0]}
        disagreements = []
        mismatches = []
        mismatch_counts = {
            "missing_local": 0,
            "missing_remote": 0,
            "shape": 0,
            "nonfinite": 0,
        }
        compared = {"actor": 0, "critic": 0}
        both_unused = 0
        for name, left in local.items():
            right = received[name]
            if left is None and right is None:
                both_unused += 1
                continue
            if left is None or right is None:
                absent = "missing_local" if left is None else "missing_remote"
                present = right if left is None else left
                mismatch_counts[absent] += 1
                if len(mismatches) < 5:
                    if isinstance(present, torch.Tensor):
                        tensor = present.detach().to(device="cpu")
                        finite = bool(torch.isfinite(tensor).all())
                        zero = bool(torch.count_nonzero(tensor) == 0) if finite else False
                        details = f"shape={tuple(tensor.shape)},finite={finite},zero={zero}"
                    else:
                        details = "present_type=" + type(present).__name__
                    mismatches.append(f"{name}:{absent}({details})")
                continue
            if (
                not isinstance(left, torch.Tensor)
                or not isinstance(right, torch.Tensor)
                or left.shape != right.shape
            ):
                mismatch_counts["shape"] += 1
                if len(mismatches) < 5:
                    mismatches.append(
                        f"{name}:shape(local={getattr(left, 'shape', None)},"
                        f"remote={getattr(right, 'shape', None)})"
                    )
                continue
            if not bool(torch.isfinite(left).all()) or not bool(torch.isfinite(right).all()):
                mismatch_counts["nonfinite"] += 1
                if len(mismatches) < 5:
                    mismatches.append(
                        f"{name}:nonfinite(local={bool(torch.isfinite(left).all())},"
                        f"remote={bool(torch.isfinite(right).all())})"
                    )
                continue
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
            compared[family] += 1
            if values[3] > 0.0:
                disagreements.append((values[3], name, values[1]))

        invalid = sum(mismatch_counts.values())
        parts = [
            "parity=" + ("parameter_mismatch" if invalid else "compared"),
            f"compared_actor={compared['actor']}",
            f"compared_critic={compared['critic']}",
            f"both_unused={both_unused}",
        ]
        parts.extend(f"{name}={count}" for name, count in mismatch_counts.items())
        if mismatches:
            parts.append("mismatch_examples=" + ";".join(mismatches))
        for family, (inner, local_sq, remote_sq, diff_sq) in families.items():
            cosine = inner / max(1e-20, math.sqrt(local_sq * remote_sq))
            relative = math.sqrt(diff_sq) / max(1e-10, math.sqrt(local_sq))
            parts.extend((
                f"{family}_cosine={cosine:.6f}",
                f"{family}_relative={relative:.6f}",
                f"{family}_local_grad_norm={math.sqrt(local_sq):.6g}",
                f"{family}_remote_grad_norm={math.sqrt(remote_sq):.6g}",
            ))
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
