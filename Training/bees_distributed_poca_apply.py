"""Authoritative Exeter-only Adam application for admitted remote MA-POCA gradients.

Never call from the WAN handler or any asynchronous worker thread. The owner of
TorchPOCAOptimizer must serialize all local and remote Adam steps on one thread.
No environment or runtime path invokes this until explicitly integrated.
"""

from __future__ import annotations

import math

from bees_distributed_poca import _named_parameters
from bees_distributed_poca_scheduler import CollectedGradient


def _step_count(value) -> int:
    if hasattr(value, "detach"):
        value = value.detach().cpu().item()
    n = int(value)
    if n < 0 or not math.isfinite(float(value)) or n != value:
        raise ValueError("Invalid persisted Adam step count")
    return n


class AuthoritativeGradientApplier:
    """Exeter owns all Adam state; remote workers only supply computed gradients."""

    def __init__(self, optimizer, *, max_optimizer_lag: int) -> None:
        if type(max_optimizer_lag) is not int or max_optimizer_lag < 0:
            raise ValueError("max_optimizer_lag must be a nonnegative integer")
        self.optimizer = optimizer
        self.max_optimizer_lag = max_optimizer_lag
        # Remember only jobs whose snapshot revisions can still pass lag checks.
        self._accepted_job_ids: dict[str, int] = {}
        self._session_id: str | None = None
        self.revision = self._infer_adam_revision()

    def _infer_adam_revision(self) -> int:
        # Adam's per-parameter 'step' is checkpointed by ML-Agents; the largest
        # count is the number of authoritative minibatch updates to date.
        state = self.optimizer.optimizer.state.values()
        steps = [
            _step_count(entry["step"])
            for entry in state
            if "step" in entry
        ]
        return max(steps, default=0)

    def start_session(self, session_id: str) -> None:
        if not session_id:
            raise ValueError("Broker session cannot be empty")
        if session_id != self._session_id:
            self._accepted_job_ids.clear()
            self._session_id = session_id

    def _prune_applied(self) -> None:
        earliest = self.revision - self.max_optimizer_lag
        self._accepted_job_ids = {
            job_id: revision
            for job_id, revision in self._accepted_job_ids.items()
            if revision >= earliest
        }

    def observe_local_step(self) -> int:
        """Call immediately after every successful LOCAL PPO Adam update."""
        self.revision += 1
        self._prune_applied()
        return self.revision

    def apply(self, collected: CollectedGradient, *, session_id: str) -> int:
        """Validate the entire gradient before changing any model or Adam state.

        This is only the eager-Adam path; CUDA graph capture must remain off.
        """
        from mlagents.torch_utils import torch
        from bees_mlagents_ppo_compat import (
            _poca_prepare_cuda_capturable_adam,
            poca_learner_optimization_options,
        )

        if self._session_id != session_id:
            raise ValueError("Broker session changed before gradient application")
        if not isinstance(collected, CollectedGradient) or collected.status != "accepted":
            raise ValueError("Only admitted remote gradient results may be applied")
        if not collected.job_id or collected.job_id in self._accepted_job_ids:
            raise ValueError("Gradient job has already been applied or lacks identity")
        if not isinstance(collected.result, dict):
            raise ValueError("Missing gradient result mapping")
        if poca_learner_optimization_options().cuda_graphs:
            raise RuntimeError("Remote Adam application is not supported with CUDA graphs")

        result = collected.result
        snapshot_revision = result.get("optimizer_revision")
        if type(snapshot_revision) is not int:
            raise ValueError("Gradient has no optimizer revision")
        lag_now = self.revision - snapshot_revision
        if lag_now < 0 or lag_now > self.max_optimizer_lag:
            raise ValueError("Gradient exceeded staleness bound before Adam application")
        if result.get("session_id") != session_id:
            raise ValueError("Gradient session mismatch")

        grads = result.get("gradients")
        named = _named_parameters(self.optimizer)
        if not isinstance(grads, dict) or set(grads) != set(named):
            raise ValueError("Gradient parameter set differs from authoritative Adam")
        prepared = {}
        for name, param in named.items():
            g = grads[name]
            if g is None:
                prepared[name] = None
                continue
            if (not isinstance(g, torch.Tensor) or g.device.type != "cpu"
                    or g.dtype != param.dtype or g.shape != param.shape
                    or not bool(torch.isfinite(g).all().item())):
                raise ValueError("Invalid remote gradient tensor for " + name)
            prepared[name] = g.to(device=param.device, copy=True)
        if all(g is None for g in prepared.values()):
            raise ValueError("Remote minibatch contained no gradients")
        if type(result.get("batch_experiences")) is not int or result["batch_experiences"] < 1:
            raise ValueError("Invalid remote gradient batch size")

        # This is the only mutation point; never manipulate Adam on workers.
        _poca_prepare_cuda_capturable_adam(self.optimizer.optimizer)
        self.optimizer.optimizer.zero_grad(set_to_none=True)
        for name, parameter in named.items():
            parameter.grad = prepared[name]
        self.optimizer.optimizer.step()
        self.revision += 1
        self._accepted_job_ids[collected.job_id] = snapshot_revision
        self._prune_applied()
        return self.revision
