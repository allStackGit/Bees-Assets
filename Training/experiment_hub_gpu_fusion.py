"""Project-neutral fused inference for independent, frozen PyTorch networks.

A trusted trainer supplies immutable, side-effect-free eval models with
sample-independent batch dimensions. Structurally compatible networks can
share GPU launch sequences through torch.func.vmap, even when each network
has too few states to use the GPU efficiently. The module never changes
training objectives, gradients, parameter values or action sampling.

The original per-model forwards remain the authoritative fallback. A new
shape is qualified against them before it can use the fused implementation,
and automatic selection uses observed end-to-end execution time rather than
GPU occupancy. No service, extra process, stream or resource lease is owned
by this module.
"""
from __future__ import annotations

from collections import OrderedDict
from copy import deepcopy
from dataclasses import dataclass
from statistics import median
from time import perf_counter
from typing import Sequence

import torch
from torch import nn
from torch.func import functional_call, stack_module_state, vmap


def _leaves(value):
    if isinstance(value, torch.Tensor):
        yield value
    elif isinstance(value, (tuple, list)):
        for part in value:
            yield from _leaves(part)
    elif isinstance(value, dict):
        for part in value.values():
            yield from _leaves(part)
    else:
        raise TypeError("Fused inference requires tensor outputs")


def _slice_output(value, index, rows, total_models):
    if isinstance(value, torch.Tensor):
        if value.ndim < 2 or value.shape[0] != total_models or value.shape[1] < rows:
            raise ValueError("Fused output is not independent per observation")
        return value[index, :rows]
    if isinstance(value, tuple):
        return tuple(_slice_output(v, index, rows, total_models) for v in value)
    if isinstance(value, list):
        return [_slice_output(v, index, rows, total_models) for v in value]
    if isinstance(value, dict):
        return {k: _slice_output(v, index, rows, total_models) for k, v in value.items()}
    raise TypeError("Unsupported fused output")


def _equivalent(expected, actual, rtol, atol):
    if type(expected) is not type(actual):
        return False
    if isinstance(expected, torch.Tensor):
        return (expected.shape == actual.shape and expected.dtype == actual.dtype
                and torch.allclose(expected, actual, rtol=rtol, atol=atol,
                                   equal_nan=False))
    if isinstance(expected, (list, tuple)):
        return len(expected) == len(actual) and all(
            _equivalent(a, b, rtol, atol) for a, b in zip(expected, actual))
    if isinstance(expected, dict):
        return expected.keys() == actual.keys() and all(
            _equivalent(expected[k], actual[k], rtol, atol) for k in expected)
    return False


def _structure(model):
    return (
        type(model),
        tuple((name, tuple(p.shape), p.dtype, p.device)
              for name, p in model.named_parameters()),
        tuple((name, tuple(b.shape), b.dtype, b.device)
              for name, b in model.named_buffers()),
    )


@dataclass(frozen=True)
class FusionDecision:
    fused: bool
    reason: str
    eager_seconds: float = 0.0
    fused_seconds: float = 0.0


class FrozenGpuFusion:
    """Reuse one packed functional model cohort over multiple inference calls.

    Models must have the same class, registered parameter/buffer structure,
    device and deterministic eval semantics. Their weights may differ. Inputs
    are one or more tensors per model, all batched on axis zero with equal
    leading dimensions within each model. Models must treat rows independently
    and may not modify buffers or use RNG. Frozen models must not be updated
    until this object is closed or replaced.

    `run([x0, x1, ...])` is shorthand for one tensor input per model.
    `run([(x0, mask0), (x1, mask1), ...])` supports multiple tensor arguments.
    Model outputs may contain tensors nested in tuples/lists/dicts; each
    output tensor must preserve the model's leading observation dimension.

    "auto" qualifies equivalence and times both implementations once per
    shape family. "fused" still requires equivalence qualification. CPU
    environments can use the same contract for qualification and debugging.
    """

    def __init__(self, models: Sequence[nn.Module], *, mode="auto",
                 max_padding_ratio=1.5, min_speedup=0.10,
                 max_stacked_bytes=128 * 1048576, max_profiles=16,
                 rtol=1e-5, atol=1e-6):
        if not isinstance(models, (list, tuple)) or not models:
            raise ValueError("A nonempty frozen model cohort is required")
        if mode not in ("auto", "eager", "fused"):
            raise ValueError("Unknown frozen fusion mode")
        if (not 1 <= max_padding_ratio <= 8 or
                not 0 <= min_speedup <= 0.5 or
                not 1 <= max_stacked_bytes <= 2**40 or
                not 1 <= max_profiles <= 256 or rtol < 0 or atol < 0):
            raise ValueError("Invalid bounded frozen-fusion budget")
        self.models = tuple(models)
        self.mode, self.max_padding_ratio = mode, max_padding_ratio
        self.min_speedup = min_speedup
        self.max_stacked_bytes, self.max_profiles = max_stacked_bytes, max_profiles
        self.rtol, self.atol = rtol, atol
        self._stacked = None
        self._prototype = None
        self._profiles = OrderedDict()
        self.calls = self.fused_calls = self.fused_model_forwards = 0
        self.eager_model_forwards = self.qualifications = 0
        self.padding_rows = self.failed_qualifications = 0
        self.qualification_seconds = 0.0
        self.last_issue = None

        base = _structure(self.models[0])
        self.device = next(iter(self.models[0].parameters()),
                           next(iter(self.models[0].buffers()), None))
        self.device = self.device.device if self.device is not None else None
        self.compatible = (len(self.models) > 1 and self.device is not None and
                           all(_structure(model) == base for model in self.models) and
                           all(not module.training for model in self.models
                               for module in model.modules()) and
                           all(not p.requires_grad for model in self.models
                               for p in model.parameters()))
        self.signature = base
        self._state_bytes = sum(p.numel()*p.element_size()
                                for p in self.models[0].parameters())*len(self.models)
        self._state_bytes += sum(b.numel()*b.element_size()
                                 for b in self.models[0].buffers())*len(self.models)
        if self._state_bytes > max_stacked_bytes:
            self.compatible = False

    @staticmethod
    def _args(batch):
        return batch if isinstance(batch, tuple) else (batch,)

    def _prepare(self, batches):
        if len(batches) != len(self.models):
            raise ValueError("Every frozen model needs one input batch")
        args = [self._args(batch) for batch in batches]
        if not all(len(item) == len(args[0]) and item for item in args):
            raise ValueError("Different frozen model input arities")
        rows = []
        for item in args:
            if any(not isinstance(t, torch.Tensor) or t.ndim < 1 or
                   t.device != self.device for t in item):
                raise ValueError("Frozen GPU inputs must be tensors on the model device")
            if len({t.shape[0] for t in item}) != 1:
                raise ValueError("A model's tensor arguments must share their row count")
            rows.append(item[0].shape[0])
        structure = tuple((t.shape[1:], t.dtype) for t in args[0])
        if any(tuple((t.shape[1:], t.dtype) for t in item) != structure
               for item in args[1:]):
            return args, rows, None
        if not all(rows) or max(rows)*len(rows) > self.max_padding_ratio*sum(rows):
            return args, rows, None
        profile = (structure, max(rows).bit_length(), len(args))
        return args, rows, profile

    def _eager(self, args):
        with torch.inference_mode():
            return [model(*parts) for model, parts in zip(self.models, args)]

    def _ensure_stacked(self):
        if self._stacked is not None:
            return
        self._prototype = deepcopy(self.models[0]).to("meta")
        self._stacked = stack_module_state(self.models)

    def _fused(self, args, rows):
        self._ensure_stacked()
        largest = max(rows)
        packed = []
        for argument in range(len(args[0])):
            pieces = []
            for parts, count in zip(args, rows):
                value = parts[argument]
                if count < largest:
                    padded = value.new_zeros((largest, *value.shape[1:]))
                    padded[:count].copy_(value)
                    value = padded
                pieces.append(value)
            packed.append(torch.stack(pieces, dim=0))
        params, buffers = self._stacked
        prototype = self._prototype

        def forward(p, b, *items):
            return functional_call(prototype, (p, b), items)

        with torch.inference_mode():
            result = vmap(forward, in_dims=(0, 0, *(0 for _ in packed)),
                          randomness="error")(params, buffers, *packed)
        return [_slice_output(result, index, count, len(self.models))
                for index, count in enumerate(rows)]

    def _finish_gpu(self):
        if self.device is not None and self.device.type == "cuda":
            torch.cuda.current_stream(self.device).synchronize()

    def _time(self, fn):
        begun = perf_counter()
        fn()
        self._finish_gpu()
        return perf_counter() - begun

    def _qualify(self, args, rows):
        begun = perf_counter()
        try:
            original = self._eager(args)
            fused = self._fused(args, rows)
            if not all(_equivalent(a, b, self.rtol, self.atol)
                       for a, b in zip(original, fused)):
                return FusionDecision(False, "numerical_mismatch")
            if self.mode == "fused":
                return FusionDecision(True, "qualified")
            # Paired wall intervals include model dispatch and CPU overhead.
            # On GPU, one synchronization per calibration sample only; none
            # is added to steady-state inference.
            eager_samples, fused_samples = [], []
            for _ in range(3):
                eager_samples.append(self._time(lambda: self._eager(args)))
                fused_samples.append(self._time(lambda: self._fused(args, rows)))
            eager, fused_time = median(eager_samples), median(fused_samples)
            return FusionDecision(
                fused_time < eager*(1-self.min_speedup),
                "faster" if fused_time < eager*(1-self.min_speedup) else "no_measured_gain",
                eager, fused_time)
        except (RuntimeError, ValueError, TypeError, NotImplementedError) as error:
            self.last_issue = str(error)[:220]
            return FusionDecision(False, "unsupported")
        finally:
            self.qualification_seconds += perf_counter()-begun

    def run(self, batches):
        args, rows, signature = self._prepare(batches)
        self.calls += 1
        if self.mode == "eager" or not self.compatible or signature is None:
            self.eager_model_forwards += len(self.models)
            return self._eager(args)
        decision = self._profiles.get(signature)
        if decision is None:
            self.qualifications += 1
            decision = self._qualify(args, rows)
            if not decision.fused:
                self.failed_qualifications += 1
            self._profiles[signature] = decision
            if len(self._profiles) > self.max_profiles:
                self._profiles.popitem(last=False)
        else:
            self._profiles.move_to_end(signature)
        if decision.fused:
            try:
                result = self._fused(args, rows)
            except (RuntimeError, ValueError, TypeError, NotImplementedError) as error:
                # Fallback is always the original frozen model on the SAME
                # immutable observations, never a resampled policy action.
                self.last_issue = str(error)[:220]
                self._profiles[signature] = FusionDecision(False, "runtime_fallback")
            else:
                self.fused_calls += 1
                self.fused_model_forwards += len(self.models)
                self.padding_rows += len(rows)*max(rows)-sum(rows)
                return result
        self.eager_model_forwards += len(self.models)
        return self._eager(args)

    def metrics(self):
        return dict(
            gpu_fusion_calls=self.calls,
            gpu_fusion_fused_calls=self.fused_calls,
            gpu_fusion_fused_model_forwards=self.fused_model_forwards,
            gpu_fusion_eager_model_forwards=self.eager_model_forwards,
            gpu_fusion_qualifications=self.qualifications,
            gpu_fusion_failed_qualifications=self.failed_qualifications,
            gpu_fusion_padding_rows=self.padding_rows,
            gpu_fusion_qualification_seconds=self.qualification_seconds,
            gpu_fusion_profiles=len(self._profiles),
        )


class FrozenGpuStreams:
    """Measured parallel CUDA/HIP streams for *different* frozen networks.

    Fusion requires identical neural structures. This separate strategy
    overlaps independent small forward graphs even when architectures,
    observations or batch sizes differ. The original serial policy forwards
    remain the reference and fallback. A caller must preserve the returned
    tensors until its own CUDA stream has consumed them; no internal
    synchronization is added to the steady-state path.

    This is a one-thread-owned executor: a trainer may create independent
    instances for its own concurrent collectors, but must not invoke the
    same instance concurrently from multiple Python threads.
    """

    def __init__(self, models: Sequence[nn.Module], *, max_streams=4,
                 min_speedup=0.10, max_profiles=16, rtol=1e-5, atol=1e-6):
        if (not isinstance(models, (list, tuple)) or not models or
                not 1 <= max_streams <= 16 or
                not 0 <= min_speedup <= 0.5 or
                not 1 <= max_profiles <= 256):
            raise ValueError("Invalid frozen multi-stream cohort or bounds")
        self.models = tuple(models)
        self.min_speedup, self.max_profiles = min_speedup, max_profiles
        self.rtol, self.atol = rtol, atol
        self.device = None
        devices = set()
        for model in self.models:
            reference = next(iter(model.parameters()),
                             next(iter(model.buffers()), None))
            if reference is not None:
                devices.add(reference.device)
        if len(devices) == 1:
            self.device = next(iter(devices))
        self.enabled = (len(self.models) >= 2 and self.device is not None and
                        self.device.type == "cuda" and
                        all(not module.training for model in self.models
                            for module in model.modules()) and
                        all(not param.requires_grad for model in self.models
                            for param in model.parameters()))
        self.streams = []
        self.max_streams = max_streams
        self.profiles = OrderedDict()
        self.calls = self.parallel_calls = self.qualifications = 0
        self.last_issue = None

    def _prepare(self, batches):
        if len(batches) != len(self.models):
            raise ValueError("Every stream model needs one batch")
        args = [FrozenGpuFusion._args(batch) for batch in batches]
        if any(not values or any(not isinstance(x, torch.Tensor)
                                  for x in values)
               for values in args):
            raise ValueError("Frozen stream inputs must be tensors")
        # A heterogeneous CPU/device cohort retains its original per-model
        # forwards. Only an admitted same-device GPU cohort may dispatch
        # those inputs to the shared parallel streams.
        if self.enabled and any(x.device != self.device for values in args
                                for x in values):
            raise ValueError("Frozen concurrent streams require one GPU device")
        signature = tuple(tuple((tuple(x.shape), x.dtype) for x in values)
                          for values in args)
        return args, signature

    def _serial(self, args):
        with torch.inference_mode():
            return [model(*parts) for model, parts in zip(self.models, args)]

    def _parallel(self, args):
        if not self.streams:
            self.streams = [torch.cuda.Stream(device=self.device)
                            for _ in range(min(self.max_streams, len(self.models)))]
        caller = torch.cuda.current_stream(self.device)
        outputs = []
        with torch.inference_mode():
            for index, (model, parts) in enumerate(zip(self.models, args)):
                stream = self.streams[index % len(self.streams)]
                stream.wait_stream(caller)
                with torch.cuda.stream(stream):
                    outputs.append(model(*parts))
                # The owner may release the source after run() returns, but
                # CUDA kernels on another stream can still be reading it.
                for tensor in parts:
                    tensor.record_stream(stream)
            for stream in self.streams:
                caller.wait_stream(stream)
            for result in outputs:
                for tensor in _leaves(result):
                    tensor.record_stream(caller)
        return outputs

    def _measure(self, fn):
        started = perf_counter()
        fn()
        torch.cuda.current_stream(self.device).synchronize()
        return perf_counter()-started

    def _qualify(self, args):
        try:
            reference = self._serial(args)
            candidate = self._parallel(args)
            if not all(_equivalent(a, b, self.rtol, self.atol)
                       for a, b in zip(reference, candidate)):
                return FusionDecision(False, "numerical_mismatch")
            serial, parallel = [], []
            for _ in range(3):
                serial.append(self._measure(lambda: self._serial(args)))
                parallel.append(self._measure(lambda: self._parallel(args)))
            baseline, candidate = median(serial), median(parallel)
            return FusionDecision(
                candidate < baseline*(1-self.min_speedup),
                "faster" if candidate < baseline*(1-self.min_speedup) else "no_measured_gain",
                baseline, candidate)
        except (RuntimeError, ValueError, TypeError, NotImplementedError) as error:
            self.last_issue = str(error)[:220]
            return FusionDecision(False, "unsupported")

    def run(self, batches):
        args, signature = self._prepare(batches)
        self.calls += 1
        if not self.enabled:
            return self._serial(args)
        decision = self.profiles.get(signature)
        if decision is None:
            self.qualifications += 1
            decision = self._qualify(args)
            self.profiles[signature] = decision
            if len(self.profiles) > self.max_profiles:
                self.profiles.popitem(last=False)
        else:
            self.profiles.move_to_end(signature)
        if decision.fused:
            try:
                result = self._parallel(args)
            except (RuntimeError, ValueError, TypeError, NotImplementedError) as error:
                self.last_issue = str(error)[:220]
                self.profiles[signature] = FusionDecision(False, "runtime_fallback")
            else:
                self.parallel_calls += 1
                return result
        return self._serial(args)

    def metrics(self):
        return dict(
            gpu_stream_calls=self.calls,
            gpu_stream_parallel_calls=self.parallel_calls,
            gpu_stream_qualifications=self.qualifications,
            gpu_stream_profiles=len(self.profiles),
        )


class FrozenGpuGraphs:
    """Shape-stable CUDA/HIP graph replay for multiple immutable small models.

    One captured graph executes the *original* independent model forwards,
    with no action sampling, reductions across policies, or optimizer work.
    Input tensors have persistent device storage. Output tensors are cloned
    after each replay: callers, recurrent state owners and IPC producers must
    never observe storage that a subsequent graph invocation can overwrite.
    PyTorch's existing allocator budget and a conservative extra-residency
    ceiling govern graph admission; capture failure returns to eager models.

    As with fused and stream inference, a trusted adapter must attest that
    these modules are deterministic frozen eval forwards. One executor has
    one caller thread and must not be invoked concurrently.
    """

    def __init__(self, models: Sequence[nn.Module], *, max_graphs=8,
                 max_capture_bytes=128*1048576, min_speedup=0.10,
                 rtol=1e-5, atol=1e-6):
        if (not isinstance(models, (list, tuple)) or not models or
                not 1 <= max_graphs <= 64 or
                not 1 <= max_capture_bytes <= 2**40 or
                not 0 <= min_speedup <= 0.5):
            raise ValueError("Invalid bounded GPU graph cohort")
        self.models = tuple(models)
        self.max_graphs, self.max_capture_bytes = max_graphs, max_capture_bytes
        self.min_speedup, self.rtol, self.atol = min_speedup, rtol, atol
        locations = {t.device for m in self.models
                     for t in (*m.parameters(), *m.buffers())}
        self.device = next(iter(locations)) if len(locations) == 1 else None
        self.enabled = (len(self.models) >= 1 and self.device is not None and
                        self.device.type == "cuda" and
                        all(not m.training for model in self.models
                            for m in model.modules()) and
                        all(not p.requires_grad for model in self.models
                            for p in model.parameters()))
        self.profiles = OrderedDict()
        self.calls = self.replays = self.qualifications = self.rejections = 0
        self.saved_launch_groups = 0
        self.last_issue = None
        self.last_graph_seconds = None
        self.last_eager_seconds = None

    def _inputs(self, batches):
        if len(batches) != len(self.models):
            raise ValueError("Graph cohort input/model count differs")
        args = [FrozenGpuFusion._args(batch) for batch in batches]
        if any(not row or any(not isinstance(t, torch.Tensor)
                              for t in row) for row in args):
            raise ValueError("Graph inputs must be tensors")
        signature = tuple(tuple((tuple(t.shape), t.dtype, t.device)
                                for t in row) for row in args)
        return args, signature

    def _eager(self, args):
        with torch.inference_mode():
            return [model(*row) for model, row in zip(self.models, args)]

    @staticmethod
    def _own(value):
        if isinstance(value, torch.Tensor):
            return value.detach().clone()
        if isinstance(value, tuple):
            parts = [FrozenGpuGraphs._own(t) for t in value]
            return type(value)(*parts) if hasattr(value, "_fields") else tuple(parts)
        if isinstance(value, list):
            return [FrozenGpuGraphs._own(t) for t in value]
        if isinstance(value, dict):
            return {k: FrozenGpuGraphs._own(t) for k, t in value.items()}
        raise TypeError("Frozen graph outputs must be tensors")

    @staticmethod
    def _copy_results(values):
        """One owned GPU allocation for compatible multi-tensor results.

        Small recurrent networks may return dozens of outputs. Cloning each
        tensor independently would add as many GPU copy launches as the
        original network's kernels. One contiguous copy owns fresh storage
        on every replay, with views restored to their original shapes and
        tuple/dict structure. Mixed types retain the independent clone path.
        """
        leaves = [tensor for value in values for tensor in _leaves(value)]
        if len(leaves)<3:
            return [FrozenGpuGraphs._own(value) for value in values]
        # Separate dtype/device banks preserve exact integer, Boolean and
        # float outputs without materializing one GPU copy for every
        # independent model's tiny tensor.
        fields, offsets, sources = [], {}, {}
        for tensor in leaves:
            key = (tensor.dtype,tensor.device)
            start = offsets.get(key,0)
            offsets[key] = start+tensor.numel()
            sources.setdefault(key,[]).append(tensor.reshape(-1))
            fields.append((key,start,tensor.numel(),tensor.shape))
        banks = {key:torch.cat(parts,dim=0)
                 for key,parts in sources.items()}
        copied = iter(banks[key].narrow(0,start,count).reshape(shape)
                      for key,start,count,shape in fields)
        def restore(value):
            if isinstance(value, torch.Tensor):
                return next(copied)
            if isinstance(value, tuple):
                parts = [restore(item) for item in value]
                return type(value)(*parts) if hasattr(value, "_fields") else tuple(parts)
            if isinstance(value, list):
                return [restore(item) for item in value]
            if isinstance(value, dict):
                return {key: restore(item) for key, item in value.items()}
            raise TypeError("Graph result contains a non-tensor value")
        return [restore(value) for value in values]

    def _capture(self, args):
        if not self.enabled or any(t.device != self.device
                                   for row in args for t in row):
            raise ValueError("CUDA/HIP graph inputs and models must use one device")
        inputs = tuple(tuple(torch.empty_like(t) for t in row) for row in args)
        static_bytes = sum(t.numel()*t.element_size()
                           for row in inputs for t in row)
        if static_bytes > self.max_capture_bytes // 2:
            raise MemoryError("GPU graph inputs exceed the residency envelope")
        for row, stable in zip(args, inputs):
            for value, target in zip(row, stable):
                target.copy_(value)
        torch.cuda.current_stream(self.device).synchronize()
        initial_reserved = torch.cuda.memory_reserved(self.device)
        initial_allocated = torch.cuda.memory_allocated(self.device)
        warmup = torch.cuda.Stream(device=self.device)
        warmup.wait_stream(torch.cuda.current_stream(self.device))
        with torch.cuda.stream(warmup), torch.inference_mode():
            # CUDA graphs require warmup outside capture; do not capture
            # the first-time library allocator or lazy operator setup.
            for _ in range(2):
                self._eager(inputs)
        warmup.synchronize()
        graph = torch.cuda.CUDAGraph()
        with torch.cuda.graph(graph, stream=warmup), torch.inference_mode():
            output = self._eager(inputs)
        warmup.synchronize()
        extra = max(
            torch.cuda.memory_reserved(self.device)-initial_reserved,
            torch.cuda.memory_allocated(self.device)-initial_allocated)
        if extra > self.max_capture_bytes:
            raise MemoryError("Captured graph exceeds its residency envelope")
        for result, row in zip(output, inputs):
            # Each return must preserve a distinct leading observation
            # dimension, but recurrent state tensors may be layer-major.
            for tensor in _leaves(result):
                if tensor.device != self.device:
                    raise ValueError("Graph result moved outside the leased GPU")
        return graph, inputs, output

    @staticmethod
    def _replay(profile, args):
        graph, inputs, output = profile
        for row, stable in zip(args, inputs):
            for tensor, target in zip(row, stable):
                target.copy_(tensor)
        graph.replay()
        return FrozenGpuGraphs._copy_results(output)

    def _elapsed(self, operation):
        torch.cuda.current_stream(self.device).synchronize()
        start = perf_counter()
        operation()
        torch.cuda.current_stream(self.device).synchronize()
        return perf_counter()-start

    def _qualify(self, args):
        try:
            original = self._eager(args)
            profile = self._capture(args)
            candidate = self._replay(profile, args)
            if not all(_equivalent(a, b, self.rtol, self.atol)
                       for a, b in zip(original, candidate)):
                return None
            # Verify *changed* observations too, not the same input
            # copied to a fresh buffer. This detects graphs that captured
            # input-dependent preprocessing as constants. Preserve reset
            # masks and recurrent states; perturb only floating observations.
            changed = [
                (row[0] + 0.125, *row[1:])
                if row[0].is_floating_point() else row
                for row in args
            ]
            changed_expected = self._eager(changed)
            changed_actual = self._replay(profile, changed)
            if not all(_equivalent(a, b, self.rtol, self.atol)
                       for a, b in zip(changed_expected, changed_actual)):
                return None
            if not all(_equivalent(a, b, self.rtol, self.atol)
                       for a, b in zip(original, candidate)):
                raise ValueError("Graph replay reused or overwrote a prior output")
            eager_times, graph_times = [], []
            for _ in range(3):
                eager_times.append(self._elapsed(lambda: self._eager(args)))
                graph_times.append(self._elapsed(lambda: self._replay(profile, args)))
            eager, graph_time = median(eager_times), median(graph_times)
            self.last_eager_seconds = eager
            self.last_graph_seconds = graph_time
            if graph_time < eager*(1-self.min_speedup):
                return profile
        except (RuntimeError, MemoryError, ValueError, TypeError,
                NotImplementedError) as error:
            self.last_issue = str(error)[:220]
        return None

    def run(self, batches):
        args, signature = self._inputs(batches)
        self.calls += 1
        if not self.enabled:
            return self._eager(args)
        if signature not in self.profiles:
            self.qualifications += 1
            profile = self._qualify(args)
            if profile is None:
                self.rejections += 1
            self.profiles[signature] = profile
            if len(self.profiles) > self.max_graphs:
                self.profiles.popitem(last=False)
        else:
            self.profiles.move_to_end(signature)
        profile = self.profiles[signature]
        if profile is not None:
            try:
                results = self._replay(profile, args)
            except (RuntimeError, ValueError, TypeError,
                    NotImplementedError) as error:
                self.last_issue = str(error)[:220]
                self.profiles[signature] = None
                self.rejections += 1
            else:
                self.replays += 1
                self.saved_launch_groups += len(self.models)
                return results
        return self._eager(args)

    def metrics(self):
        return dict(
            gpu_graph_calls=self.calls,
            gpu_graph_replays=self.replays,
            gpu_graph_qualifications=self.qualifications,
            gpu_graph_rejections=self.rejections,
            gpu_graph_profiles=sum(x is not None for x in self.profiles.values()),
            gpu_graph_saved_launch_groups=self.saved_launch_groups,
        )


class MeasuredGpuExecution:
    """Project-independent eager/fused/parallel-stream inference selector.

    One immutable cohort is qualified against original per-model forwards.
    The measured fastest safe GPU strategy is selected for each input shape,
    subject to the same minimum speedup threshold. Future projects need only
    pass their frozen eval networks and tensor batches; the coordinator does
    not need to know the game's actor architecture or PPO configuration.
    """

    def __init__(self, models: Sequence[nn.Module], *, min_speedup=0.10,
                 max_profiles=16, max_streams=4, max_padding_ratio=1.5,
                 max_stacked_bytes=128*1048576, rtol=1e-5, atol=1e-6,
                 allow_fusion=True, allow_graphs=True):
        # Some backends already provide an exact specialized grouped kernel.
        # Their generic fallback should try independent GPU streams without
        # retaining a second stacked model population.
        if type(allow_fusion) is not bool or type(allow_graphs) is not bool:
            raise ValueError("GPU execution strategies must be explicitly enabled")
        self.allow_fusion = allow_fusion
        self.allow_graphs = allow_graphs
        self.fusion = FrozenGpuFusion(
            models, min_speedup=min_speedup, max_profiles=max_profiles,
            max_padding_ratio=max_padding_ratio,
            max_stacked_bytes=max_stacked_bytes, rtol=rtol, atol=atol)
        self.streams = FrozenGpuStreams(
            models, min_speedup=min_speedup, max_profiles=max_profiles,
            max_streams=max_streams, rtol=rtol, atol=atol)
        self.graphs = FrozenGpuGraphs(
            models, max_graphs=min(max_profiles, 8),
            max_capture_bytes=max_stacked_bytes,
            min_speedup=min_speedup, rtol=rtol, atol=atol)
        self.max_profiles = max_profiles
        self.choices = OrderedDict()
        self.calls = self.eager_calls = self.fused_calls = self.stream_calls = self.graph_calls = 0
        self.weight_refreshes = self.refresh_validations = self.refresh_invalidations = 0
        self._pending_fused_validation = False
        self._pending_graph_validation = set()

    def refresh_frozen_weights(self, changed_indices=None):
        """Refresh existing GPU storage without rebuilding captured graphs.

        The caller must have drained all previous inference, synchronized
        its GPU stream, and already copied newer immutable weights into
        the SAME model objects. This preserves the CUDA graph's parameter
        addresses. Invalid captured output behavior is detected against
        fresh eager model forwards on the first use of each retained shape.
        """
        indices = (tuple(range(len(self.fusion.models))) if changed_indices is None
                   else tuple(changed_indices))
        if (not indices or len(set(indices)) != len(indices) or
                any(type(i) is not int or not 0 <= i < len(self.fusion.models)
                    for i in indices)):
            raise ValueError("Invalid frozen-weight refresh model indices")
        if self.fusion._stacked is not None:
            # torch.func.stack_module_state owns independent stacked storage.
            # Without copying refreshed weights here, vmap would silently
            # continue using the OLD policy while graph/stream use the new.
            params, buffers = self.fusion._stacked
            named_params = [dict(model.named_parameters())
                            for model in self.fusion.models]
            named_buffers = [dict(model.named_buffers())
                             for model in self.fusion.models]
            with torch.no_grad():
                for name, packed in params.items():
                    for index in indices:
                        packed[index].copy_(named_params[index][name])
                for name, packed in buffers.items():
                    for index in indices:
                        packed[index].copy_(named_buffers[index][name])
            self._pending_fused_validation = True
        self._pending_graph_validation.update(
            signature for signature, profile in self.graphs.profiles.items()
            if profile is not None)
        self.weight_refreshes += 1

    @staticmethod
    def _signature(batches):
        return tuple(tuple((tuple(t.shape), t.dtype, t.device)
                           for t in FrozenGpuFusion._args(batch))
                     for batch in batches)

    def run(self, batches):
        if len(batches) != len(self.fusion.models):
            raise ValueError("GPU inference cohort and input count differ")
        signature = self._signature(batches)
        self.calls += 1
        choice = self.choices.get(signature)
        if choice is None:
            choice, best_seconds = "eager", float("inf")
            # Only GPU jobs need the extra first-shape calibration. CPU and
            # mixed-device cohorts stay on the original serial model calls.
            if self.fusion.device is not None and self.fusion.device.type == "cuda":
                if self.allow_fusion and self.fusion.compatible:
                    # Equal network structure does not guarantee that two
                    # collectors supply the same number of tensor arguments.
                    # This is a normal unfusable cohort, not a reason to
                    # reject independently executable streams or graphs.
                    try:
                        args, rows, fused_shape = self.fusion._prepare(batches)
                    except ValueError:
                        fused_shape = None
                    if fused_shape is not None:
                        self.fusion.qualifications += 1
                        decision = self.fusion._qualify(args, rows)
                        self.fusion._profiles[fused_shape] = decision
                        if decision.fused:
                            choice, best_seconds = "fused", decision.fused_seconds
                if self.streams.enabled:
                    stream_args, stream_shape = self.streams._prepare(batches)
                    self.streams.qualifications += 1
                    decision = self.streams._qualify(stream_args)
                    self.streams.profiles[stream_shape] = decision
                    if decision.fused and decision.fused_seconds < best_seconds:
                        choice, best_seconds = "streams", decision.fused_seconds
                if self.allow_graphs and self.graphs.enabled:
                    graph_args, graph_shape = self.graphs._inputs(batches)
                    self.graphs.qualifications += 1
                    profile = self.graphs._qualify(graph_args)
                    if profile is None:
                        self.graphs.rejections += 1
                    self.graphs.profiles[graph_shape] = profile
                    if profile is not None and self.graphs.last_graph_seconds < best_seconds:
                        choice = "graph"
                        best_seconds = self.graphs.last_graph_seconds
            self.choices[signature] = choice
            if len(self.choices) > self.max_profiles:
                self.choices.popitem(last=False)
        else:
            self.choices.move_to_end(signature)
        if choice == "fused":
            before = self.fusion.fused_calls
            result = self.fusion.run(batches)
            if self.fusion.fused_calls == before:
                self.choices[signature] = "eager"
                self.eager_calls += 1
            else:
                if self._pending_fused_validation:
                    self._pending_fused_validation = False
                    expected = self.fusion._eager([
                        FrozenGpuFusion._args(batch) for batch in batches])
                    self.refresh_validations += 1
                    if not all(_equivalent(a,b,self.fusion.rtol,self.fusion.atol)
                               for a,b in zip(expected,result)):
                        # A custom module may have captured constant weights.
                        # Fall back to the authoritative new frozen models.
                        self.refresh_invalidations += 1
                        self.choices[signature] = "eager"
                        self.fusion._profiles.clear()
                        self.eager_calls += 1
                        return expected
                self.fused_calls += 1
            return result
        if choice == "streams":
            before = self.streams.parallel_calls
            result = self.streams.run(batches)
            if self.streams.parallel_calls == before:
                self.choices[signature] = "eager"
                self.eager_calls += 1
            else:
                self.stream_calls += 1
            return result
        if choice == "graph":
            before = self.graphs.replays
            result = self.graphs.run(batches)
            if self.graphs.replays == before:
                self.choices[signature] = "eager"
                self.eager_calls += 1
            else:
                if signature in self._pending_graph_validation:
                    self._pending_graph_validation.discard(signature)
                    original = self.graphs._eager([
                        FrozenGpuFusion._args(batch) for batch in batches])
                    self.refresh_validations += 1
                    if not all(_equivalent(a,b,self.graphs.rtol,self.graphs.atol)
                               for a,b in zip(original,result)):
                        self.refresh_invalidations += 1
                        self.graphs.profiles.pop(signature,None)
                        self.choices.pop(signature,None)
                        self.eager_calls += 1
                        return original
                self.graph_calls += 1
            return result
        self.eager_calls += 1
        return self.fusion._eager([
            FrozenGpuFusion._args(batch) for batch in batches])

    def metrics(self):
        return dict(
            gpu_execution_calls=self.calls,
            gpu_execution_eager_calls=self.eager_calls,
            gpu_execution_fused_calls=self.fused_calls,
            gpu_execution_stream_calls=self.stream_calls,
            gpu_execution_graph_calls=self.graph_calls,
            gpu_execution_profiles=len(self.choices),
            gpu_execution_weight_refreshes=self.weight_refreshes,
            gpu_execution_refresh_validations=self.refresh_validations,
            gpu_execution_refresh_invalidations=self.refresh_invalidations,
            **self.fusion.metrics(),
            **self.streams.metrics(),
            **self.graphs.metrics(),
        )
