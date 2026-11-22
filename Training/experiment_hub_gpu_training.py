"""Project-independent concurrent execution of disjoint GPU optimizer steps.

Each submitted callable performs exactly ONE real training update, including
its own forward, backward and optimizer step. No step is replayed, duplicated,
resampled or rolled back during qualification. A fixed GPU cohort alternates
serial and stream-overlapped windows, then selects the faster observed useful
work rate. PPO hyperparameters, clipping, gradients and policy lag remain
owned by the original trainer.

The caller must ensure a single thread owns this executor; optimizer/model
states are disjoint; callbacks use fixed, already-sampled inputs; and they
do not consume global RNG or depend on another update in the same call.
Resource leases and GPU device identity remain the Hub agent's authority.
"""
from __future__ import annotations

from statistics import median
from threading import Lock
from time import perf_counter
from typing import Callable, Sequence

import torch
from torch import nn


def _tensor_outputs(value):
    if isinstance(value, torch.Tensor):
        yield value
    elif isinstance(value, (tuple, list)):
        for part in value:
            yield from _tensor_outputs(part)
    elif isinstance(value, dict):
        for part in value.values():
            yield from _tensor_outputs(part)


def _storage_identity(tensor):
    if tensor.numel() == 0:
        return None
    return tensor.untyped_storage().data_ptr()


class IndependentGpuTraining:
    """Measure-and-select execution for genuinely independent GPU learners.

    Unlike inference-only graph capture, this accepts a complete live
    optimizer-step callback per model. All registered optimizers and model
    parameter/buffer storage must be disjoint. Input preparation, snapshots,
    checkpointing and action sampling must occur outside run().

    Serial and concurrent calibration execute *different real updates*
    exactly once each. They never clone or revert optimizer state. The
    adaptive choice uses useful work per actual elapsed training-step wall,
    not GPU occupancy. Four serial and four parallel windows are the default
    qualification period; steady state has no device-wide synchronization.
    """

    def __init__(self, models: Sequence[nn.Module],
                 optimizers: Sequence[torch.optim.Optimizer], *,
                 admit_independent_updates=False, max_streams=4,
                 sample_windows=4, min_speedup=0.10):
        if not admit_independent_updates:
            raise ValueError("Independent optimizer execution needs explicit adapter approval")
        if (not isinstance(models, (list, tuple)) or
                not isinstance(optimizers, (list, tuple)) or
                len(models) != len(optimizers) or
                not 2 <= len(models) <= 16 or
                not 2 <= max_streams <= 16 or
                not 2 <= sample_windows <= 32 or
                not 0.05 <= min_speedup <= 0.5):
            raise ValueError("Invalid fixed optimizer execution cohort")
        self.models, self.optimizers = tuple(models), tuple(optimizers)
        self.max_streams, self.sample_windows = max_streams, sample_windows
        self.min_speedup = min_speedup
        identities, device, owned_optimizers = set(), None, set()
        forbidden_random = (nn.Dropout, nn.Dropout1d, nn.Dropout2d, nn.Dropout3d,
                            nn.AlphaDropout, nn.FeatureAlphaDropout, nn.RReLU)
        for model, optimizer in zip(self.models, self.optimizers):
            if not isinstance(model, nn.Module) or not isinstance(optimizer, torch.optim.Optimizer):
                raise ValueError("A registered training step needs an owned model and optimizer")
            if id(optimizer) in owned_optimizers:
                raise ValueError("Independent learners cannot share an optimizer instance")
            owned_optimizers.add(id(optimizer))
            if any(isinstance(module, forbidden_random) and module.training
                   for module in model.modules()):
                raise ValueError("GPU stream training requires explicit RNG-free learner forwards")
            params = tuple(model.parameters())
            if not params:
                raise ValueError("A training cohort model must own parameters")
            param_ids = {id(p) for p in params}
            owned = [p for group in optimizer.param_groups for p in group["params"]]
            if not owned or len({id(p) for p in owned}) != len(owned) or any(
                    id(p) not in param_ids for p in owned):
                raise ValueError("Optimizer updates must be owned by their registered model")
            # Optimizer momentum, variance and step-state tensors are part
            # of the independent update ownership contract too. Sharing
            # an Adam state allocation between learners would race even
            # when their model parameter objects are distinct.
            state_tensors = tuple(
                state_value for state in optimizer.state.values()
                for state_value in state.values()
                if isinstance(state_value, torch.Tensor))
            for tensor in (*params, *model.buffers()):
                if device is None:
                    device = tensor.device
                if tensor.device != device:
                    raise ValueError("All independent learner parameters must use one GPU")
                ptr = _storage_identity(tensor)
                key = (str(tensor.device), ptr)
                if ptr is not None and key in identities:
                    raise ValueError("Independent learners cannot share parameter/buffer storage")
                if ptr is not None:
                    identities.add(key)
            for tensor in state_tensors:
                # Standard Adam may maintain its scalar step count on the
                # CPU while every parameter and accumulator lives on GPU.
                if tensor.device not in (device, torch.device("cpu")):
                    raise ValueError("Optimizer state belongs to another GPU device")
                ptr = _storage_identity(tensor)
                key = (str(tensor.device), ptr)
                if ptr is not None and key in identities:
                    raise ValueError("Independent optimizers cannot share state storage")
                if ptr is not None:
                    identities.add(key)
        if device is None or device.type != "cuda":
            raise ValueError("Concurrent optimizer steps require a CUDA/HIP device")
        self.device = device
        self.streams = []
        self.serial_rates, self.concurrent_rates = [], []
        self.phase = "serial_probe"
        self.calls = self.serial_calls = self.concurrent_calls = 0
        self.work_dispatched = 0.0
        self.last_qualified_seconds = 0.0
        self.last_qualified_work_rate = 0.0
        self.selected = None
        self._busy = Lock()

    def _parallel(self, steps, inputs):
        caller = torch.cuda.current_stream(self.device)
        if not self.streams:
            self.streams = [
                torch.cuda.Stream(device=self.device)
                for _ in range(min(self.max_streams, len(self.models)))
            ]
        result = []
        for index, (step, tensors) in enumerate(zip(steps, inputs)):
            stream = self.streams[index % len(self.streams)]
            stream.wait_stream(caller)
            with torch.cuda.stream(stream):
                result.append(step())
            # The caller may discard a minibatch after run() returns even
            # though CUDA kernels on the independent stream are still
            # consuming it. Register that lifetime with the allocator.
            for tensor in tensors:
                tensor.record_stream(stream)
        for stream in self.streams:
            caller.wait_stream(stream)
        for value in result:
            for tensor in _tensor_outputs(value):
                tensor.record_stream(caller)
        return result

    @staticmethod
    def _serial(steps):
        return [step() for step in steps]

    def _settle(self):
        baseline = median(self.serial_rates)
        parallel = median(self.concurrent_rates)
        self.selected = "parallel" if parallel >= baseline*(1+self.min_speedup) else "serial"
        self.phase = "settled"

    def run(self, steps: Sequence[Callable[[], object]], *,
            inputs, work_units=None):
        if not self._busy.acquire(blocking=False):
            raise RuntimeError("Independent GPU optimizer executor is already in use")
        try:
            return self._run(steps, inputs, work_units)
        finally:
            self._busy.release()

    def _run(self, steps, inputs, work_units):
        if (not isinstance(steps, (list, tuple)) or
                len(steps) != len(self.models) or
                any(not callable(step) for step in steps)):
            raise ValueError("One optimizer-step callback is needed per registered model")
        # Every live minibatch source buffer that callbacks read must be
        # registered for stream lifetime. An explicit empty tuple is valid
        # only for updates without external input tensors.
        if (not isinstance(inputs, (tuple, list)) or len(inputs)!=len(steps) or
                any(not isinstance(row, (tuple, list)) or
                    any(not isinstance(t, torch.Tensor) or t.device != self.device
                        for t in row) for row in inputs)):
            raise ValueError("Provide every per-step GPU input tensor for safe stream ownership")
        if work_units is None:
            work_units = (1,)*len(steps)
        if (not isinstance(work_units, (list, tuple)) or
                len(work_units) != len(steps) or
                any(type(x) not in (int, float) or not 0 < x <= 1e12
                    for x in work_units)):
            raise ValueError("Training work must be reported in positive fixed units")
        count = float(sum(work_units))
        if self.phase == "settled":
            mode = self.selected
        else:
            mode = "serial" if self.phase == "serial_probe" else "parallel"
        # Measure actual elapsed GPU service including CPU dispatch and
        # outstanding kernels, but synchronize ONLY during qualification.
        # Settled updates retain asynchronous caller-stream dependencies.
        measuring = self.phase != "settled"
        if measuring:
            torch.cuda.current_stream(self.device).synchronize()
        begun = perf_counter()
        output = self._parallel(steps, inputs) if mode == "parallel" else self._serial(steps)
        if measuring:
            torch.cuda.current_stream(self.device).synchronize()
        seconds = perf_counter()-begun
        self.calls += 1
        self.work_dispatched += count
        self.serial_calls += int(mode == "serial")
        self.concurrent_calls += int(mode == "parallel")
        if measuring and seconds>0:
            self.last_qualified_seconds = seconds
            self.last_qualified_work_rate = count/seconds
        if measuring and seconds>0:
            rates = self.serial_rates if mode == "serial" else self.concurrent_rates
            rates.append(count/seconds)
            if self.phase == "serial_probe" and len(self.serial_rates)>=self.sample_windows:
                self.phase = "parallel_probe"
            elif self.phase == "parallel_probe" and len(self.concurrent_rates)>=self.sample_windows:
                self._settle()
        return output

    def metrics(self):
        return dict(
            gpu_training_step_batches=self.calls,
            gpu_training_serial_batches=self.serial_calls,
            gpu_training_parallel_batches=self.concurrent_calls,
            gpu_training_work_dispatched=self.work_dispatched,
            gpu_training_calibration_serial=len(self.serial_rates),
            gpu_training_calibration_parallel=len(self.concurrent_rates),
            gpu_training_selected_parallel=int(self.selected == "parallel"),
            gpu_training_calibration_complete=int(self.phase == "settled"),
            gpu_training_last_qualified_seconds=self.last_qualified_seconds,
            gpu_training_last_qualified_work_per_second=self.last_qualified_work_rate,
        )
