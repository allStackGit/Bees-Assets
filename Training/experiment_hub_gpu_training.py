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

from collections import deque
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
                 sample_windows=4, min_speedup=0.10,
                 completion_sample_interval=16, retune_interval=256,
                 retune_threshold=0.25):
        if not admit_independent_updates:
            raise ValueError("Independent optimizer execution needs explicit adapter approval")
        if (not isinstance(models, (list, tuple)) or
                not isinstance(optimizers, (list, tuple)) or
                len(models) != len(optimizers) or
                not 2 <= len(models) <= 16 or
                not 2 <= max_streams <= 16 or
                not 2 <= sample_windows <= 32 or
                not 0.05 <= min_speedup <= 0.5 or
                not 4 <= completion_sample_interval <= 256 or
                not 64 <= retune_interval <= 65536 or
                not 0.10 <= retune_threshold <= 0.75):
            raise ValueError("Invalid fixed optimizer execution cohort")
        self.models, self.optimizers = tuple(models), tuple(optimizers)
        self.max_streams, self.sample_windows = max_streams, sample_windows
        self.min_speedup = min_speedup
        self.completion_sample_interval = completion_sample_interval
        self.retune_interval, self.retune_threshold = retune_interval, retune_threshold
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
        ceiling = min(self.max_streams, len(self.models))
        self.stream_candidates = sorted(set(
            [ceiling]+[n for n in (2, 4, 8, 16) if n<=ceiling]))
        # Interleave the serial reference and each stream width. Running
        # four serial samples, then four of each width, confounds training
        # progression and changing GPU contention with the strategy itself.
        self.probe_widths = [0, *self.stream_candidates]
        self.probe_index = 0
        self.serial_rates = []
        self.parallel_rates = {n:[] for n in self.stream_candidates}
        self.phase = "calibrating"
        self.calls = self.serial_calls = self.concurrent_calls = 0
        self.work_dispatched = 0.0
        self.work_confirmed = 0.0
        self._pending_samples = deque()
        self._recent_gpu_rates = deque(maxlen=12)
        self._sample_generation = 0
        self._settled_at_call = 0
        self._completion_samples = 0
        self._completion_sample_skips = 0
        self._retunes = 0
        self._sampling_enabled = True
        self.live_gpu_rate = 0.0
        self.last_qualified_seconds = 0.0
        self.last_qualified_work_rate = 0.0
        self.selected = None
        self.selected_streams = 0
        self._busy = Lock()
        self._closed = False

    def _parallel(self, steps, inputs, width):
        caller = torch.cuda.current_stream(self.device)
        if not self.streams:
            self.streams = [
                torch.cuda.Stream(device=self.device)
                for _ in range(min(self.max_streams, len(self.models)))
            ]
        result = []
        for index, (step, tensors) in enumerate(zip(steps, inputs)):
            stream = self.streams[index % width]
            stream.wait_stream(caller)
            with torch.cuda.stream(stream):
                result.append(step())
            # The caller may discard a minibatch after run() returns even
            # though CUDA kernels on the independent stream are still
            # consuming it. Register that lifetime with the allocator.
            for tensor in tensors:
                tensor.record_stream(stream)
        for stream in self.streams[:width]:
            caller.wait_stream(stream)
        for value in result:
            for tensor in _tensor_outputs(value):
                tensor.record_stream(caller)
        return result

    @staticmethod
    def _serial(steps):
        return [step() for step in steps]

    def _settle(self):
        best = median(self.serial_rates)
        chosen = 0
        for width in self.stream_candidates:
            observed = median(self.parallel_rates[width])
            # Require material improvement over the original optimizer
            # schedule, then prefer fewer streams until adding more
            # consistently improves useful work by another five percent.
            margin = self.min_speedup if chosen==0 else 0.05
            if observed >= best*(1+margin):
                best, chosen = observed, width
        self.selected_streams = chosen
        self.selected = "parallel" if chosen else "serial"
        self.phase = "settled"
        self._settled_at_call = self.calls
        self._recent_gpu_rates.clear()
        self._sample_generation += 1

    def _collect_completed(self):
        # A sample event follows the streams' caller-side wait edges.
        # Thus event.query() confirms ALL previously submitted work on
        # this executor's caller stream, without a host synchronization.
        while self._pending_samples and self._pending_samples[0][1].query():
            start, end, work_mark, batch_work, generation = self._pending_samples.popleft()
            self.work_confirmed = max(self.work_confirmed, work_mark)
            seconds = start.elapsed_time(end) / 1000.0
            if seconds > 0 and generation == self._sample_generation:
                self._completion_samples += 1
                self._recent_gpu_rates.append(batch_work / seconds)
        if self._recent_gpu_rates:
            self.live_gpu_rate = median(self._recent_gpu_rates)
        if (self.phase != "settled" or
                self.calls-self._settled_at_call < self.retune_interval or
                len(self._recent_gpu_rates) < 8):
            return
        measured = self.live_gpu_rate
        selected_rates = (self.parallel_rates[self.selected_streams]
                          if self.selected_streams else self.serial_rates)
        baseline = median(selected_rates)
        if (baseline > 0 and (measured < baseline*(1-self.retune_threshold) or
                              measured > baseline*(1+self.retune_threshold))):
            # Re-qualify using future distinct optimizer updates. No
            # captured update is ever rerun, reset, or checkpoint-replayed.
            self._retunes += 1
            self.serial_rates.clear()
            for rates in self.parallel_rates.values():
                rates.clear()
            self.probe_index = 0
            self.phase = "calibrating"
            self._recent_gpu_rates.clear()
            self.live_gpu_rate = 0.0
            self._sample_generation += 1

    def run(self, steps: Sequence[Callable[[], object]], *,
            inputs, work_units=None):
        if not self._busy.acquire(blocking=False):
            raise RuntimeError("Independent GPU optimizer executor is already in use")
        try:
            if self._closed:
                raise RuntimeError("Independent GPU optimizer executor was closed")
            return self._run(steps, inputs, work_units)
        finally:
            self._busy.release()

    def close(self):
        with self._busy:
            if self._closed:
                return
            # Kernel submissions may still be asynchronous. The owner
            # cannot release its optimizer state or OS GPU lease until
            # all dependent stream work has finished.
            torch.cuda.current_stream(self.device).synchronize()
            self.work_confirmed = self.work_dispatched
            self._pending_samples.clear()
            self._closed = True
            self.streams.clear()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def _run(self, steps, inputs, work_units):
        self._collect_completed()
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
            mode, width = self.selected, self.selected_streams
        else:
            width = self.probe_widths[self.probe_index]
            mode = "serial" if width == 0 else "parallel"
        # Measure actual elapsed GPU service including CPU dispatch and
        # outstanding kernels, but synchronize ONLY during qualification.
        # Settled updates retain asynchronous caller-stream dependencies.
        measuring = self.phase != "settled"
        if measuring:
            torch.cuda.current_stream(self.device).synchronize()
        sample = None
        if (not measuring and self._sampling_enabled and
                (self.calls+1) % self.completion_sample_interval == 0):
            if len(self._pending_samples) < 32:
                try:
                    start_event = torch.cuda.Event(enable_timing=True)
                    end_event = torch.cuda.Event(enable_timing=True)
                    start_event.record(torch.cuda.current_stream(self.device))
                    sample = (start_event, end_event)
                except (RuntimeError, NotImplementedError):
                    # Some accelerator builds do not expose timing events.
                    # Execution stays asynchronous; only telemetry stops.
                    self._sampling_enabled = False
            else:
                self._completion_sample_skips += 1
        begun = perf_counter()
        output = (self._parallel(steps, inputs, width)
                  if mode == "parallel" else self._serial(steps))
        if sample is not None:
            try:
                sample[1].record(torch.cuda.current_stream(self.device))
            except (RuntimeError, NotImplementedError):
                # Training kernels have already been submitted. A missing
                # timing-event capability must not turn a valid update into
                # a failed or repeated optimization step.
                self._sampling_enabled = False
                sample = None
        if measuring:
            torch.cuda.current_stream(self.device).synchronize()
        seconds = perf_counter()-begun
        self.calls += 1
        self.work_dispatched += count
        if measuring:
            self.work_confirmed = self.work_dispatched
        if sample is not None:
            self._pending_samples.append((
                sample[0], sample[1], self.work_dispatched, count,
                self._sample_generation))
        self.serial_calls += int(mode == "serial")
        self.concurrent_calls += int(mode == "parallel")
        if measuring and seconds>0:
            self.last_qualified_seconds = seconds
            self.last_qualified_work_rate = count/seconds
        if measuring and seconds>0:
            rates = self.serial_rates if mode == "serial" else self.parallel_rates[width]
            rates.append(count/seconds)
            self.probe_index = (self.probe_index+1) % len(self.probe_widths)
            if (len(self.serial_rates)>=self.sample_windows and
                    all(len(rates)>=self.sample_windows
                        for rates in self.parallel_rates.values())):
                self._settle()
        return output

    def metrics(self):
        # A training callback may request metrics while run() owns the
        # executor. Do not deadlock it on the same non-reentrant lock.
        if self._busy.acquire(blocking=False):
            try:
                if not self._closed:
                    self._collect_completed()
            finally:
                self._busy.release()
        return dict(
            gpu_training_step_batches=self.calls,
            gpu_training_serial_batches=self.serial_calls,
            gpu_training_parallel_batches=self.concurrent_calls,
            gpu_training_work_dispatched=self.work_dispatched,
            gpu_training_work_confirmed=self.work_confirmed,
            gpu_training_gpu_completion_samples=self._completion_samples,
            gpu_training_completion_sample_skips=self._completion_sample_skips,
            gpu_training_pending_completion_events=len(self._pending_samples),
            gpu_training_live_work_per_second=self.live_gpu_rate,
            gpu_training_retunes=self._retunes,
            gpu_training_calibration_serial=len(self.serial_rates),
            gpu_training_calibration_parallel=sum(map(len,self.parallel_rates.values())),
            gpu_training_selected_parallel=int(self.selected == "parallel"),
            gpu_training_selected_streams=self.selected_streams,
            gpu_training_stream_candidates=len(self.stream_candidates),
            gpu_training_calibration_complete=int(self.phase == "settled"),
            gpu_training_last_qualified_seconds=self.last_qualified_seconds,
            gpu_training_qualified_work_per_second=self.last_qualified_work_rate,
        )
