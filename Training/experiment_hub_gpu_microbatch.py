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


def _unpad_output(value, actual, padded):
    """Restore the observation axis without copying owned GPU output."""
    if isinstance(value, torch.Tensor):
        if value.ndim < 1 or value.shape[0] != padded:
            raise ValueError("Padded model did not preserve the observation batch axis")
        return value[:actual]
    if isinstance(value, tuple):
        parts = [_unpad_output(part, actual, padded) for part in value]
        return type(value)(*parts) if hasattr(value, "_fields") else tuple(parts)
    if isinstance(value, list):
        return [_unpad_output(part, actual, padded) for part in value]
    if isinstance(value, dict):
        return {key:_unpad_output(part, actual, padded)
                for key,part in value.items()}
    raise TypeError("Padded model outputs must contain tensors")


def _stable_batch_rows(rows, limit):
    """Conservative GPU graph shapes without large empty minibatches."""
    quantum = (1 << (rows-1).bit_length()) if rows < 8 else (
        8 if rows < 64 else 32 if rows < 256 else 64)
    target = ((rows+quantum-1)//quantum)*quantum
    return target if target <= limit and target <= rows*1.5 else rows


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
    input_bytes: int
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
                 max_batch_mb=64, max_pending_mb=256,
                 wait_ms=0.25, max_cohorts=8, max_workspace_mb=512,
                 allow_graphs=True):
        if (not isinstance(models, dict) or not models or
                set(models) != set(versions) or
                not 1 <= max_pending <= 65536 or
                not 1 <= max_requests <= max_pending or
                not 1 <= max_batch_rows <= 1048576 or
                not 1 <= max_batch_mb <= max_pending_mb <= 16384 or
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
        self.max_batch_bytes = int(max_batch_mb*1048576)
        self.max_pending_bytes = int(max_pending_mb*1048576)
        self.max_cohorts, self.allow_graphs = max_cohorts, allow_graphs
        # Cap each retained model cohort's graph/state residency. Every
        # cohort may retain eight independent shape profiles; reserve a
        # ninth share for its stacked weights and helper buffers.
        self.cohort_budget_bytes = int(
            max_workspace_mb*1048576/(max_cohorts*9))
        self._pending, self._executors = deque(), OrderedDict()
        self._active_group = []
        self._admission = OrderedDict()
        self._last_requal_group = 0
        self._condition = Condition()
        self._closed = False
        self._publishing = False
        self._last_event = None
        self._outstanding = 0
        self._outstanding_bytes = 0
        self._producer_recent = {}
        self._multi_producer = False
        self._counters = dict(submitted=0, dispatched=0, model_forwards=0,
                              groups=0, coalesced=0, rejected=0, fallbacks=0,
                              max_queue_depth=0, admitted_profiles=0,
                              rejected_profiles=0, qualification_seconds=0.0,
                              profile_refreshes=0, bucketed_groups=0,
                              flat_groups=0, flat_packed_bytes=0,
                              head_of_line_bypasses=0, policy_refreshes=0,
                              policy_refresh_seconds=0.0,
                              policy_refresh_bytes=0)
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
            if model_id not in self.models:
                raise ValueError("Frozen model membership changed")
            args = inputs if isinstance(inputs, tuple) else (inputs,)
            if not args or any(not isinstance(x, torch.Tensor) or x.ndim < 1
                               or x.device != self.device for x in args):
                raise ValueError("Frozen inference inputs must be device tensors with a batch axis")
            rows = args[0].shape[0]
            if not 1 <= rows <= self.max_batch_rows or any(x.shape[0] != rows for x in args):
                raise ValueError("Input rows exceed the fixed GPU batch envelope")
            byte_count = sum(t.numel()*t.element_size() for t in args)
            if byte_count > self.max_batch_bytes:
                raise ValueError("One inference request exceeds the GPU batch byte envelope")
            checked.append((model_id,args,rows,byte_count,version))
        if self.device.type == "cuda":
            ready = torch.cuda.Event()
            ready.record(torch.cuda.current_stream(self.device))
        futures = [BatchFuture(self.device) for _ in checked]
        with self._condition:
            while self._publishing and not self._closed:
                self._condition.wait()
            if self._closed:
                raise RuntimeError("Frozen inference batcher was closed")
            # Capture a single atomic policy generation for the whole
            # submission. A blocked submitter cannot enqueue stale weights
            # after publication completes.
            if any(self.versions[model_id] != version for
                   model_id,_,_,_,version in checked):
                raise ValueError("Frozen inference policy version changed")
            if (self._outstanding+len(checked)>self.max_pending or
                    self._outstanding_bytes+sum(row[3] for row in checked) >
                    self.max_pending_bytes):
                self._counters["rejected"] += len(checked)
                raise RuntimeError("Frozen inference queue reached its capacity")
            producer, timestamp = get_ident(), perf_counter()
            # A historic second thread must not impose a batching delay
            # on a lone active collector for the rest of a training run.
            self._producer_recent = {
                owner:seen for owner,seen in self._producer_recent.items()
                if timestamp-seen <= 0.02}
            self._producer_recent[producer] = timestamp
            if len(self._producer_recent)>64:
                self._producer_recent = dict(sorted(
                    self._producer_recent.items(),key=lambda item:item[1],
                    reverse=True)[:64])
            self._multi_producer |= len(self._producer_recent)>1
            self._pending.extend(_Request(model_id,args,rows,byte_count,future,ready)
                                 for (model_id,args,rows,byte_count,_),future
                                 in zip(checked,futures))
            self._outstanding += len(checked)
            self._outstanding_bytes += sum(row[3] for row in checked)
            self._counters["submitted"] += len(checked)
            self._counters["max_queue_depth"] = max(
                self._counters["max_queue_depth"], self._outstanding)
            self._condition.notify()
        return futures

    def publish_weights(self, changes):
        """Publish new frozen weights at one drained policy-version boundary.

        changes maps model IDs to (new_version, complete state_dict). The
        input weights are validated before publication. No old-version work
        is cancelled, replayed or silently executed with new weights: the
        worker drains accepted requests first, while new submissions wait.
        Parameter/buffer storage addresses remain stable so qualified GPU
        graphs can be reused; fused stacked weights are refreshed too.
        This should be called only by the trusted snapshot owner, not from
        a Future completion callback running on the batcher worker thread.
        """
        if current_thread() is self._worker:
            raise RuntimeError("A GPU worker cannot publish its own policy")
        if not isinstance(changes, dict) or not changes:
            raise ValueError("Publish a nonempty frozen policy snapshot mapping")
        prepared = {}
        for name, value in changes.items():
            if name not in self.models or not isinstance(value, (tuple,list)) or len(value)!=2:
                raise ValueError("Unknown frozen policy or invalid snapshot envelope")
            version, new_state = value
            if (type(version) not in (str,int) or
                    (isinstance(version,str) and not 1<=len(version)<=128) or
                    (isinstance(version,int) and not 0<=version<=2**63-1) or
                    not isinstance(new_state, dict)):
                raise ValueError("A policy snapshot needs a bounded immutable version")
            expected = self.models[name].state_dict()
            if expected.keys()!=new_state.keys():
                raise ValueError("Policy parameter/buffer keys changed")
            for field,old in expected.items():
                tensor = new_state[field]
                if (not isinstance(tensor, torch.Tensor) or
                        tensor.shape != old.shape or tensor.dtype != old.dtype or
                        tensor.device not in (self.device,torch.device("cpu"))):
                    raise ValueError("Policy parameter/buffer identity changed")
            prepared[name] = (version,new_state)
        with self._condition:
            while self._publishing and not self._closed:
                self._condition.wait()
            if self._closed:
                raise RuntimeError("Frozen GPU service was closed")
            if any(self.versions[name]==version
                   for name,(version,_) in prepared.items()):
                raise ValueError("New frozen policy version must differ")
            self._publishing = True
            while self._outstanding and not self._closed:
                self._condition.wait()
            if self._closed:
                self._publishing = False
                self._condition.notify_all()
                raise RuntimeError("Frozen GPU service stopped during publication")
            final_event = self._last_event
        begun = perf_counter()
        try:
            context = (torch.cuda.device(self.device) if self.device.type=="cuda"
                       else nullcontext())
            with context:
                if final_event is not None:
                    final_event.synchronize()
                storage_before = {
                    name:{k:t.untyped_storage().data_ptr()
                          for k,t in self.models[name].state_dict().items()
                          if t.numel()}
                    for name in prepared
                }
                with torch.no_grad():
                    for name,(_,weights) in prepared.items():
                        # assign=False preserves the captured graph's weight
                        # pointers; a custom module replacing them is denied.
                        self.models[name].load_state_dict(
                            weights,strict=True,assign=False)
                    for keys,executor in self._executors.items():
                        dirty = [i for i,name in enumerate(keys) if name in prepared]
                        if dirty:
                            executor.refresh_frozen_weights(dirty)
                for name in prepared:
                    after = {k:t.untyped_storage().data_ptr()
                             for k,t in self.models[name].state_dict().items()
                             if t.numel()}
                    if after != storage_before[name]:
                        raise RuntimeError("Frozen policy weights moved during graph publication")
                if self.device.type=="cuda":
                    torch.cuda.current_stream(self.device).synchronize()
            with self._condition:
                for name,(version,_) in prepared.items():
                    self.versions[name] = version
                self._counters["policy_refreshes"] += len(prepared)
                self._counters["policy_refresh_bytes"] += sum(
                    t.numel()*t.element_size() for _,weights in prepared.values()
                    for t in weights.values())
                self._counters["policy_refresh_seconds"] += perf_counter()-begun
        except BaseException:
            # In-place state copies cannot be rolled back reliably if an
            # unknown custom module failed midway. Stop admitting work
            # rather than expose a mixed version to another collector.
            with self._condition:
                self._closed = True
            raise
        finally:
            with self._condition:
                self._publishing = False
                self._condition.notify_all()

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
                self._outstanding_bytes = 0
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
                recent = perf_counter()
                active_producers = sum(
                    recent-seen <= 0.02
                    for seen in self._producer_recent.values())
                delay = self.wait_seconds if active_producers>1 else 0.0
                deadline = perf_counter()+delay
                group, layouts, rows, batch_bytes = [], {}, 0, 0
                while len(group) < self.max_requests:
                    if self._pending:
                        deferred, blocked = deque(), set()
                        # Scanning a bounded prefix exposes other compatible
                        # networks behind a shape/byte-budget mismatch.
                        # Never overtake a deferred request for the SAME
                        # frozen model: its collector may depend on order.
                        inspected = 0
                        while (self._pending and inspected < 64 and
                               len(group) < self.max_requests):
                            item = self._pending.popleft()
                            inspected += 1
                            shape = tuple((x.shape[1:], x.dtype)
                                          for x in item.inputs)
                            fits = (item.model_id not in blocked and
                                    rows+item.rows <= self.max_batch_rows and
                                    batch_bytes+item.input_bytes <= self.max_batch_bytes and
                                    (item.model_id not in layouts or
                                     layouts[item.model_id] == shape))
                            if not fits:
                                deferred.append(item)
                                blocked.add(item.model_id)
                                continue
                            if deferred:
                                self._counters["head_of_line_bypasses"] += 1
                            self._active_group = group
                            layouts[item.model_id] = shape
                            group.append(item)
                            rows += item.rows
                            batch_bytes += item.input_bytes
                        deferred.extend(self._pending)
                        self._pending = deferred
                        if (self._pending or rows >= self.max_batch_rows or
                                batch_bytes >= self.max_batch_bytes):
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
                    self._outstanding_bytes -= sum(item.input_bytes for item in group)
                    self._active_group = []
                    self._condition.notify_all()
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

    @staticmethod
    def _flat_model_inputs(owners, keys):
        """Pack all compatible input tensors with one torch.cat per dtype.

        This avoids separate tiny GPU concatenation launches for every
        independent network and recurrent state. Returned model arguments
        are zero-copy views into the owned dtype bank. Noncontiguous inputs
        retain the original per-argument packer.
        """
        fragments, descriptors, offsets = OrderedDict(), [], {}
        total_inputs = 0
        for key in keys:
            members = owners[key]
            fields = []
            for position in range(len(members[0].inputs)):
                chunks = [item.inputs[position] for item in members]
                if any(not t.is_contiguous() for t in chunks):
                    raise ValueError("Flat pack requires contiguous GPU tensors")
                dtype = chunks[0].dtype
                if any(t.dtype!=dtype for t in chunks):
                    raise ValueError("Frozen model inputs changed dtype")
                shape = (sum(item.rows for item in members),*chunks[0].shape[1:])
                count = sum(t.numel() for t in chunks)
                start = offsets.get(dtype,0)
                offsets[dtype] = start+count
                fragments.setdefault(dtype,[]).extend(t.reshape(-1) for t in chunks)
                fields.append((dtype,start,count,shape))
                total_inputs += len(chunks)
            descriptors.append(fields)
        if total_inputs<2:
            raise ValueError("No small input launches available to consolidate")
        banks = {dtype:torch.cat(parts,dim=0)
                 for dtype,parts in fragments.items()}
        return [tuple(banks[dtype].narrow(0,start,count).view(shape)
                      for dtype,start,count,shape in fields)
                for fields in descriptors]

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
        def grouped(bucket=False, flat=False):
            if bucket and flat:
                raise ValueError("Flat GPU packing and stable padding are alternative strategies")
            inputs = (self._flat_model_inputs(owners,keys) if flat else [])
            original_rows, target_rows = [], []
            for index,key in enumerate(keys):
                members = owners[key]
                if not flat:
                    inputs.append(tuple(
                        torch.cat([item.inputs[i] for item in members], dim=0)
                        if len(members)>1 else members[0].inputs[i]
                        for i in range(len(members[0].inputs))))
                rows = sum(item.rows for item in members)
                target = _stable_batch_rows(rows, self.max_batch_rows) if bucket else rows
                original_rows.append(rows)
                target_rows.append(target)
            if bucket:
                padded_rows = sum(target_rows)
                padded_bytes = sum(
                    target*sum(t[0].numel()*t.element_size() for t in row)
                    for row,target in zip(inputs,target_rows))
                if (padded_rows > self.max_batch_rows or
                        padded_bytes > self.max_batch_bytes):
                    raise ValueError("Shape-stable padding exceeds GPU batch lease")
                for idx,(row,actual,target) in enumerate(
                        zip(inputs,original_rows,target_rows)):
                    if target==actual:
                        continue
                    inputs[idx] = tuple(torch.cat([
                        t, t.new_zeros((target-actual,*t.shape[1:]))
                    ], dim=0) for t in row)
            values = executor.run(inputs)
            resolved = {}
            for idx,(key,value) in enumerate(zip(keys,values)):
                members = owners[key]
                actual, target = original_rows[idx],target_rows[idx]
                if target!=actual:
                    value = _unpad_output(value, actual, target)
                pieces = _split_results(value, [item.rows for item in members])
                for item,piece in zip(members,pieces):
                    resolved[id(item)] = piece
            return [resolved[id(item)] for item in group]

        mode, output = None, None
        # Even a single small collector can reuse a stable captured GPU
        # graph across changing rollout lengths. More than one collector
        # can additionally share one batched model invocation.
        eligible = len(group)>1 or self.allow_graphs
        if eligible:
            shape = tuple(
                (key, sum(item.rows for item in members).bit_length(),
                 len(members).bit_length(),
                 tuple((tuple(t.shape[1:]),str(t.dtype))
                       for t in members[0].inputs))
                for key in keys for members in (owners[key],))
            mode = self._admission.get(shape)
            if (mode is not None and
                    self._counters["groups"]-self._last_requal_group>=256):
                # Workload mix / co-tenants can change after an earlier
                # decision. Reconsider one cached family per 256 completed
                # groups, never restart or recapture on every request.
                mode = None
                self._last_requal_group = self._counters["groups"]
                self._counters["profile_refreshes"] += 1
            if mode is None:
                started = perf_counter()
                try:
                    reference = self._individual(group)
                    candidate = grouped()
                    if not all(_equivalent(a,b,1e-5,1e-6)
                               for a,b in zip(reference,candidate)):
                        raise ValueError("Grouped inference changed frozen policy outputs")
                    choices = {
                        "individual":lambda:self._individual(group),
                        "exact":lambda:grouped(),
                    }
                    validated = {"individual":reference,"exact":candidate}
                    if self.allow_graphs:
                        try:
                            padded = grouped(bucket=True)
                            if all(_equivalent(a,b,1e-5,1e-6)
                                   for a,b in zip(reference,padded)):
                                choices["bucket"] = lambda:grouped(bucket=True)
                                validated["bucket"] = padded
                        except (ValueError,TypeError,RuntimeError,
                                torch.OutOfMemoryError):
                            pass
                    if self.device.type=="cuda":
                        try:
                            flat_output = grouped(flat=True)
                            if all(_equivalent(a,b,1e-5,1e-6)
                                   for a,b in zip(reference,flat_output)):
                                choices["flat"] = lambda:grouped(flat=True)
                                validated["flat"] = flat_output
                        except (ValueError,TypeError,RuntimeError,
                                torch.OutOfMemoryError):
                            pass
                    times = {name:[] for name in choices}
                    for _ in range(2):
                        # Interleaved timed trials reduce the chance of
                        # choosing a GPU path merely because a different
                        # job temporarily stopped competing for the device.
                        for name,operation in choices.items():
                            seconds,_ = self._time(operation)
                            times[name].append(seconds)
                    baseline = median(times["individual"])
                    faster = sorted((median(v),name) for name,v in times.items()
                                    if name!="individual" and
                                    median(v)<baseline*0.95)
                    mode = faster[0][1] if faster else "individual"
                    output = validated.get(mode)
                except (ValueError,TypeError,RuntimeError,
                        torch.OutOfMemoryError):
                    mode = "individual"
                    output = self._individual(group)
                    self._counters["fallbacks"] += 1
                self._counters["qualification_seconds"] += perf_counter()-started
                self._counters["admitted_profiles" if mode!="individual" else
                               "rejected_profiles"] += 1
                self._admission[shape] = mode
                if len(self._admission)>self.max_cohorts*8:
                    self._admission.popitem(last=False)
            else:
                self._admission.move_to_end(shape)
        if output is None:
            if mode in ("exact","bucket","flat"):
                try:
                    output = grouped(bucket=mode=="bucket",flat=mode=="flat")
                except (ValueError,TypeError,RuntimeError,
                        torch.OutOfMemoryError):
                    output = self._individual(group)
                    self._counters["fallbacks"] += 1
                    if eligible:
                        self._admission[shape] = "individual"
                    mode = "individual"
            else:
                output = self._individual(group)
        if mode=="bucket":
            self._counters["bucketed_groups"] += 1
        if mode=="flat":
            self._counters["flat_groups"] += 1
            self._counters["flat_packed_bytes"] += sum(
                item.input_bytes for item in group)
        if mode in ("exact","bucket","flat"):
            self._counters["coalesced"] += len(group)-len(keys)
        resolved = {id(item):value for item,value in zip(group,output)}
        event = None
        if stream is not None:
            event = torch.cuda.Event()
            event.record(stream)
        self._last_event = event
        self._counters["model_forwards"] += (
            len(keys) if mode in ("exact","bucket","flat") else len(group))
        for item in group:
            item.future.done_event = event
            item.future.set_result(resolved[id(item)])

    def metrics(self):
        with self._condition:
            values = dict(self._counters, pending=self._outstanding,
                          cached_model_cohorts=len(self._executors),
                          pending_input_bytes=self._outstanding_bytes,
                          calibrated_batch_shapes=len(self._admission),
                          multiple_producers=int(self._multi_producer),
                          active_producers=sum(
                              perf_counter()-seen <= 0.02
                              for seen in self._producer_recent.values()))
            return {"gpu_microbatch_"+name:value
                    for name,value in values.items()}

    def close(self, *, cancel_pending=False):
        cancelled = []
        with self._condition:
            while self._publishing and not self._closed:
                self._condition.wait()
            self._closed = True
            if cancel_pending:
                cancelled = list(self._pending)
                self._pending.clear()
                self._outstanding -= len(cancelled)
                self._outstanding_bytes -= sum(item.input_bytes for item in cancelled)
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
