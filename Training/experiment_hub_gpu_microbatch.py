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
from statistics import median
from threading import Condition, Thread, current_thread, get_ident
from time import perf_counter

import torch
from torch import nn

from experiment_hub_gpu_fusion import MeasuredGpuExecution, _leaves, _equivalent


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

    def cancel(self):
        # The service cannot safely retract a GPU kernel after admission.
        # A consumer must close/cancel the queue, not cancel an individual
        # Future and accidentally invalidate other members of its batch.
        return False

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
                 wait_ms=0.25, max_cohorts=8, max_workspace_mb=512,
                 allow_graphs=True):
        if (not isinstance(models, dict) or not models or
                set(models) != set(versions) or
                not 1 <= max_pending <= 65536 or
                not 1 <= max_requests <= max_pending or
                not 1 <= max_batch_rows <= 1048576 or
                not 0 <= wait_ms <= 50 or
                not 1 <= max_cohorts <= 256 or
                not 16 <= max_workspace_mb <= 16384):
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
        # Cap each retained model cohort's graph/state residency. Every
        # cohort may retain eight independent shape profiles; reserve a
        # ninth share for its stacked weights and helper buffers.
        self.cohort_budget_bytes = int(
            max_workspace_mb*1048576/(max_cohorts*9))
        self._pending, self._executors = deque(), OrderedDict()
        self._active_group = []
        self._admission = OrderedDict()
        self._condition = Condition()
        self._closed = False
        self._outstanding = 0
        self._first_producer = None
        self._multi_producer = False
        self._counters = dict(submitted=0, dispatched=0, model_forwards=0,
                              groups=0, coalesced=0, rejected=0, fallbacks=0,
                              max_queue_depth=0, admitted_profiles=0,
                              rejected_profiles=0, qualification_seconds=0.0)
        self._worker = Thread(target=self._serve, name="hub-gpu-microbatch", daemon=True)
        self._worker.start()

    def submit(self, model_id: str, inputs, *, version) -> BatchFuture:
        return self.submit_many([(model_id, inputs, version)])[0]

    def submit_many(self, requests):
        """Atomically enqueue independent inference work with one GPU event.

        Each entry is (model_id, tensor_or_tuple_of_tensors, frozen_version).
        Batches are never partially admitted: insufficient capacity rejects
        the whole call. This lets an existing collector coalesce many small
        policies without launching a producer thread for each network.
        """
        if not isinstance(requests, (list, tuple)) or not 1 <= len(requests) <= self.max_requests:
            raise ValueError("Supply a bounded nonempty collection of frozen inference requests")
        ready = None
        checked = []
        for model_id, inputs, version in requests:
            if model_id not in self.models or self.versions[model_id] != version:
                raise ValueError("Frozen model version/membership changed")
            args = inputs if isinstance(inputs, tuple) else (inputs,)
            if not args or any(not isinstance(x, torch.Tensor) or x.ndim < 1
                               or x.device != self.device for x in args):
                raise ValueError("Frozen inference inputs must be device tensors with a batch axis")
            rows = args[0].shape[0]
            if not 1 <= rows <= self.max_batch_rows or any(x.shape[0] != rows for x in args):
                raise ValueError("Input rows exceed the fixed GPU batch envelope")
            checked.append((model_id,args,rows))
        if self.device.type == "cuda":
            ready = torch.cuda.Event()
            ready.record(torch.cuda.current_stream(self.device))
        futures = [BatchFuture(self.device) for _ in checked]
        with self._condition:
            if self._closed:
                raise RuntimeError("Frozen inference batcher was closed")
            if self._outstanding+len(checked)>self.max_pending:
                self._counters["rejected"] += len(checked)
                raise RuntimeError("Frozen inference queue reached its capacity")
            producer = get_ident()
            if self._first_producer is None:
                self._first_producer = producer
            elif producer != self._first_producer:
                self._multi_producer = True
            self._pending.extend(_Request(model_id,args,rows,future,ready)
                                 for (model_id,args,rows),future in zip(checked,futures))
            self._outstanding += len(checked)
            self._counters["submitted"] += len(checked)
            self._counters["max_queue_depth"] = max(
                self._counters["max_queue_depth"], self._outstanding)
            self._condition.notify()
        return futures

    def _serve(self):
        context = torch.cuda.device(self.device) if self.device.type == "cuda" else nullcontext()
        try:
            with context:
                self._drain()
                # close() is an ownership boundary: finish GPU submissions
                # before the worker or its parent resource lease is released.
                if self.device.type == "cuda":
                    torch.cuda.synchronize(self.device)
        except BaseException as error:
            # An unexpected worker initialization/device failure must not
            # strand pending Futures forever. Deliver exceptions without
            # holding Condition, since callbacks may reenter this service.
            with self._condition:
                self._closed = True
                stranded = list(self._pending)+self._active_group
                self._pending.clear()
                self._outstanding = 0
                self._condition.notify_all()
            for item in stranded:
                if not item.future.done():
                    item.future.set_exception(error)

    def _drain(self):
        while True:
            with self._condition:
                while not self._pending and not self._closed:
                    self._condition.wait()
                if not self._pending:
                    return
                # A single caller already has submit_many() to publish a
                # complete batch atomically. Never introduce a timer wait
                # on its serial critical path. Independent collector
                # threads unlock the short coalescing window automatically.
                delay = self.wait_seconds if self._multi_producer else 0.0
                deadline = perf_counter()+delay
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
                        self._active_group = group
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
                    self._active_group = []
                    self._counters["dispatched"] += len(group)
                    self._counters["groups"] += 1

    def _time(self, action):
        if self.device.type == "cuda":
            torch.cuda.synchronize(self.device)
        start = perf_counter()
        output = action()
        if self.device.type == "cuda":
            torch.cuda.synchronize(self.device)
        return perf_counter()-start, output

    def _individual(self, group):
        with torch.inference_mode():
            return [self.models[item.model_id](*item.inputs) for item in group]

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
        # Arrival order across unrelated collectors is not a different
        # execution graph. Canonicalize cohort membership to reuse one
        # set of GPU weights/streams/captures independent of submit order.
        keys = tuple(sorted(owners))
        executor = self._executors.get(keys)
        if executor is None:
            executor = MeasuredGpuExecution(
                [self.models[key] for key in keys],
                allow_graphs=self.allow_graphs,
                max_stacked_bytes=self.cohort_budget_bytes)
            self._executors[keys] = executor
            if len(self._executors) > self.max_cohorts:
                # Graphs and their static storage must outlive all pending
                # kernels. Join only this worker's GPU stream before evicting
                # the least recently used cohort (never the active one).
                if stream is not None:
                    stream.synchronize()
                self._executors.popitem(last=False)
        else:
            self._executors.move_to_end(keys)
        def grouped():
            inputs = []
            for key in keys:
                members = owners[key]
                inputs.append(tuple(
                    torch.cat([item.inputs[i] for item in members], dim=0)
                    if len(members)>1 else members[0].inputs[i]
                    for i in range(len(members[0].inputs))))
            values = executor.run(inputs)
            resolved = {}
            for key, value in zip(keys, values):
                members = owners[key]
                pieces = _split_results(value, [item.rows for item in members])
                for item, piece in zip(members, pieces):
                    resolved[id(item)] = piece
            return [resolved[id(item)] for item in group]

        mode = "grouped"
        output = None
        if len(group)>1:
            # Same shape/job multiset has the same GPU execution cost
            # regardless of which concurrent collector arrived first.
            # Stringify dtypes so heterogeneous layout keys are sortable.
            shape = tuple(sorted(
                (item.model_id,item.rows,
                 tuple((tuple(t.shape[1:]),str(t.dtype))
                       for t in item.inputs))
                for item in group))
            mode = self._admission.get(shape)
            if mode is None:
                started = perf_counter()
                try:
                    candidate = grouped()
                    reference = self._individual(group)
                    if not all(_equivalent(a,b,1e-5,1e-6)
                               for a,b in zip(reference,candidate)):
                        raise ValueError("Coalesced inference changed frozen model outputs")
                    grouped_times, solo_times = [], []
                    for _ in range(2):
                        t,_ = self._time(grouped)
                        grouped_times.append(t)
                        t,_ = self._time(lambda:self._individual(group))
                        solo_times.append(t)
                    # Packing, splitting and GPU completion belong in
                    # the actual service-cost comparison. A fast model
                    # forward alone is not enough to admit coalescing.
                    mode = ("grouped" if median(grouped_times) <
                            median(solo_times)*0.95 else "individual")
                    output = candidate if mode=="grouped" else reference
                except (ValueError, TypeError, RuntimeError,
                        torch.OutOfMemoryError) as error:
                    mode = "individual"
                    output = self._individual(group)
                    self._counters["fallbacks"] += 1
                self._counters["qualification_seconds"] += perf_counter()-started
                self._counters["admitted_profiles" if mode=="grouped" else
                               "rejected_profiles"] += 1
                self._admission[shape] = mode
                if len(self._admission)>self.max_cohorts*8:
                    self._admission.popitem(last=False)
            else:
                self._admission.move_to_end(shape)
        if output is None:
            if mode=="grouped":
                try:
                    output = grouped()
                except (ValueError, TypeError, RuntimeError,
                        torch.OutOfMemoryError):
                    output = self._individual(group)
                    self._counters["fallbacks"] += 1
                    if len(group)>1:
                        self._admission[shape] = "individual"
                    mode = "individual"
            else:
                output = self._individual(group)
        resolved = {id(item):value for item,value in zip(group,output)}
        event = None
        if stream is not None:
            event = torch.cuda.Event()
            event.record(stream)
        self._counters["model_forwards"] += len(keys) if mode=="grouped" else len(group)
        if mode=="grouped":
            self._counters["coalesced"] += len(group)-len(keys)
        for item in group:
            item.future.done_event = event
            item.future.set_result(resolved[id(item)])

    def metrics(self):
        with self._condition:
            values = dict(self._counters, pending=self._outstanding,
                          cached_model_cohorts=len(self._executors),
                          calibrated_batch_shapes=len(self._admission),
                          multiple_producers=int(self._multi_producer))
            return {"gpu_microbatch_"+name:value
                    for name,value in values.items()}

    def close(self, *, cancel_pending=False):
        cancelled = []
        with self._condition:
            self._closed = True
            if cancel_pending:
                cancelled = list(self._pending)
                self._pending.clear()
                self._outstanding -= len(cancelled)
            self._condition.notify_all()
        for item in cancelled:
            # Future callbacks may query metrics or close the queue, so
            # never call them under the service's condition lock.
            item.future.set_exception(RuntimeError(
                "Frozen batcher closed before execution"))
        if self._worker is not current_thread():
            self._worker.join()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
