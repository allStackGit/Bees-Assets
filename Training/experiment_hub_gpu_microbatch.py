"""Bounded project-neutral inference coalescing for immutable PyTorch policies.

Multiple independent collectors submit work asynchronously, with one GPU
owner combining compatible requests from the SAME frozen model before choosing
the Hub's measured eager/fused/stream/graph execution strategy. This is an
in-process data plane, not a machine/job scheduler: the caller still owns
OS leases, policy versions, learner/collector lag, and action sampling.

Every model must be frozen, in eval mode, and deterministic per observation.
All model arguments and returned tensors must use axis zero for independent
observations when more than one request is coalesced. A model returning
layer-major recurrent state may instead adapt its forward to batch-major
state, or use distinct model keys so its requests are not concatenated.
"""
from __future__ import annotations

from collections import OrderedDict, deque
from concurrent.futures import Future
from contextlib import nullcontext
from dataclasses import dataclass
from threading import Condition, Thread
from time import perf_counter

import torch
from torch import nn

from experiment_hub_gpu_fusion import MeasuredGpuExecution, _leaves


def _split_results(value, sizes):
    if len(sizes) == 1:
        return [value]
    if isinstance(value, torch.Tensor):
        if not value.ndim or value.shape[0] != sum(sizes):
            raise ValueError("Coalesced network output is not observation-batched")
        return list(value.split(sizes, dim=0))
    if isinstance(value, (tuple, list)):
        parts = [_split_results(x, sizes) for x in value]
        return [type(value)(*(item[i] for item in parts))
                if hasattr(value, "_fields") else
                type(value)(item[i] for item in parts)
                for i in range(len(sizes))]
    if isinstance(value, dict):
        parts = {key: _split_results(x, sizes) for key, x in value.items()}
        return [{key: items[i] for key, items in parts.items()}
                for i in range(len(sizes))]
    raise TypeError("Coalesced forward outputs must be tensors")


class BatchFuture(Future):
    """A completion that transfers producer stream ownership to its consumer."""

    def __init__(self, device):
        super().__init__()
        self.device = device
        self.done_event = None

    def result(self, timeout=None):
        output = super().result(timeout)
        if self.done_event is not None:
            stream = torch.cuda.current_stream(self.device)
            stream.wait_event(self.done_event)
            for tensor in _leaves(output):
                tensor.record_stream(stream)
        return output


@dataclass
class _Request:
    model_id: str
    inputs: tuple
    rows: int
    future: BatchFuture
    ready: object


class FrozenInferenceMicrobatcher:
    """Collect compatible frozen-model requests into bounded GPU forward groups.

    submit() never synchronizes the GPU or waits for other collectors. Use its
    Future.result() from the consuming thread: it inserts a CUDA stream wait,
    not a device-wide barrier. Input tensors must remain unmodified until the
    returned Future completes. The batcher retains their storage and records
    the producer/consumer stream dependencies.

    A dedicated worker owns the GPU forward executor and all captured graphs.
    Configuration bounds queue memory, batch size, batch-wait latency, and
    retained model-cohort profiles. No network endpoint or GPU process is
    created. Close the batcher before releasing its parent native GPU lease.
    """

    def __init__(self, models: dict[str, nn.Module], versions: dict[str, object],
                 *, max_pending=256, max_requests=32, max_batch_rows=4096,
                 wait_ms=1.0, max_cohorts=16, allow_graphs=True):
        if (not isinstance(models, dict) or not models or
                set(models) != set(versions) or
                not 1 <= max_pending <= 65536 or
                not 1 <= max_requests <= max_pending or
                not 1 <= max_batch_rows <= 1048576 or
                not 0 <= wait_ms <= 50 or
                not 1 <= max_cohorts <= 256):
            raise ValueError("Invalid frozen inference batcher configuration")
        self.models = dict(models)
        self.versions = dict(versions)
        locations = {tensor.device for model in models.values()
                     for tensor in (*model.parameters(), *model.buffers())}
        if (len(locations) != 1 or
                any(not isinstance(key, str) or not key or
                    not isinstance(model, nn.Module) or
                    any(layer.training for layer in model.modules()) or
                    any(p.requires_grad for p in model.parameters())
                    for key, model in models.items())):
            raise ValueError("Only frozen deterministic models on one device may share a batcher")
        self.device = next(iter(locations))
        self.max_pending, self.max_requests = max_pending, max_requests
        self.max_batch_rows, self.wait_seconds = max_batch_rows, wait_ms/1000
        self.max_cohorts, self.allow_graphs = max_cohorts, allow_graphs
        self._pending, self._executors = deque(), OrderedDict()
        self._condition = Condition()
        self._closed = False
        self._outstanding = 0
        self._counters = dict(submitted=0, dispatched=0, model_forwards=0,
                              groups=0, coalesced=0, rejected=0, fallbacks=0,
                              max_queue_depth=0)
        self._worker = Thread(target=self._serve, name="hub-gpu-microbatch", daemon=True)
        self._worker.start()

    def submit(self, model_id: str, inputs, *, version) -> BatchFuture:
        if model_id not in self.models or self.versions[model_id] != version:
            raise ValueError("Frozen model version/membership changed")
        args = inputs if isinstance(inputs, tuple) else (inputs,)
        if not args or any(not isinstance(x, torch.Tensor) or x.ndim < 1
                           or x.device != self.device for x in args):
            raise ValueError("Frozen inference inputs must be device tensors with a batch axis")
        rows = args[0].shape[0]
        if not 1 <= rows <= self.max_batch_rows or any(x.shape[0] != rows for x in args):
            raise ValueError("Input rows exceed the fixed GPU batch envelope")
        ready = None
        if self.device.type == "cuda":
            ready = torch.cuda.Event()
            ready.record(torch.cuda.current_stream(self.device))
        future = BatchFuture(self.device)
        with self._condition:
            if self._closed:
                raise RuntimeError("Frozen inference batcher was closed")
            if self._outstanding >= self.max_pending:
                self._counters["rejected"] += 1
                raise RuntimeError("Frozen inference queue reached its capacity")
            self._pending.append(_Request(model_id, args, rows, future, ready))
            self._outstanding += 1
            self._counters["submitted"] += 1
            self._counters["max_queue_depth"] = max(
                self._counters["max_queue_depth"], self._outstanding)
            self._condition.notify()
        return future

    def _serve(self):
        context = torch.cuda.device(self.device) if self.device.type == "cuda" else nullcontext()
        with context:
            while True:
                with self._condition:
                    while not self._pending and not self._closed:
                        self._condition.wait()
                    if not self._pending:
                        return
                    deadline = perf_counter()+self.wait_seconds
                    group, layouts, rows = [], {}, 0
                    while len(group) < self.max_requests:
                        if self._pending:
                            item = self._pending[0]
                            shape = tuple((x.shape[1:], x.dtype) for x in item.inputs)
                            if (rows+item.rows > self.max_batch_rows or
                                    item.model_id in layouts and
                                    layouts[item.model_id] != shape):
                                break
                            self._pending.popleft()
                            layouts[item.model_id] = shape
                            group.append(item)
                            rows += item.rows
                            if rows >= self.max_batch_rows:
                                break
                        else:
                            remaining = deadline-perf_counter()
                            if remaining <= 0 or self._closed:
                                break
                            self._condition.wait(remaining)
                try:
                    self._dispatch(group)
                except BaseException as error:
                    for item in group:
                        if not item.future.done():
                            item.future.set_exception(error)
                finally:
                    with self._condition:
                        self._outstanding -= len(group)
                        self._counters["dispatched"] += len(group)
                        self._counters["groups"] += 1

    def _dispatch(self, group):
        stream = (torch.cuda.current_stream(self.device)
                  if self.device.type == "cuda" else None)
        owners = OrderedDict()
        for item in group:
            owners.setdefault(item.model_id, []).append(item)
            if item.ready is not None:
                stream.wait_event(item.ready)
            if stream is not None:
                for tensor in item.inputs:
                    tensor.record_stream(stream)
        keys = tuple(owners)
        executor = self._executors.get(keys)
        if executor is None:
            executor = MeasuredGpuExecution(
                [self.models[key] for key in keys],
                allow_graphs=self.allow_graphs)
            self._executors[keys] = executor
            if len(self._executors) > self.max_cohorts:
                self._executors.popitem(last=False)
        else:
            self._executors.move_to_end(keys)
        inputs = []
        for members in owners.values():
            inputs.append(tuple(
                torch.cat([item.inputs[i] for item in members], dim=0)
                if len(members)>1 else members[0].inputs[i]
                for i in range(len(members[0].inputs))))
        result = executor.run(inputs)
        resolved = {}
        for key, output in zip(keys, result):
            members = owners[key]
            try:
                pieces = _split_results(output, [item.rows for item in members])
            except (ValueError, TypeError):
                # Unsupported state axis: preserve exact independent forwards.
                with torch.inference_mode():
                    pieces = [self.models[key](*item.inputs) for item in members]
                self._counters["fallbacks"] += 1
            for item, piece in zip(members, pieces):
                resolved[id(item)] = piece
        event = None
        if stream is not None:
            event = torch.cuda.Event()
            event.record(stream)
        self._counters["model_forwards"] += len(keys)
        self._counters["coalesced"] += len(group)-len(keys)
        for item in group:
            item.future.done_event = event
            item.future.set_result(resolved[id(item)])

    def metrics(self):
        with self._condition:
            return dict(self._counters, pending=self._outstanding,
                        cached_model_cohorts=len(self._executors))

    def close(self, *, cancel_pending=False):
        with self._condition:
            self._closed = True
            if cancel_pending:
                while self._pending:
                    item = self._pending.popleft()
                    item.future.set_exception(RuntimeError("Frozen batcher closed before execution"))
                    self._outstanding -= 1
            self._condition.notify_all()
        if self._worker is not __import__("threading").current_thread():
            self._worker.join()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
