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
from contextlib import contextmanager
from functools import lru_cache
from math import isfinite
from numbers import Integral
from concurrent.futures import ThreadPoolExecutor, wait
from threading import current_thread
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


def _state_tensors(value):
    """Visit nested optimizer state without duplicating aliased tensors."""
    pending, seen = [value], set()
    while pending:
        item = pending.pop()
        if isinstance(item, torch.Tensor):
            yield item
        elif isinstance(item, (dict,list,tuple)):
            if id(item) in seen:
                continue
            seen.add(id(item))
            pending.extend(item.values() if isinstance(item,dict) else item)


def _storage_identity(tensor):
    if tensor.numel() == 0:
        return None
    return tensor.untyped_storage().data_ptr()


def immutable_training_input_budget(free_bytes, desired_bytes, reuse_passes, *,
                                    max_resident_bytes=2 * 1024**3,
                                    free_fraction=0.18,
                                    minimum_compute_bytes=2 * 1024**3):
    """Bound reusable GPU input residency without reserving activation VRAM.

    Intended for immutable rollout/input tensors consumed repeatedly during
    one training update. Dynamic free_bytes should already honor an external
    GPU memory lease (e.g., an allocator cap) when one exists. The cap is an
    *allocation ceiling*, not a reservation. Callers allocate incrementally
    and fall back to streamed inputs if device allocations fail.

    A single use has no GPU cache benefit. Preserve both a fixed compute
    reserve and at least 82% of reported free bytes for backprop, optimizer
    state, inference and other co-located GPU jobs.
    """
    if (not all(type(x) is int and x >= 0 for x in
                (free_bytes, desired_bytes, reuse_passes, max_resident_bytes,
                 minimum_compute_bytes)) or
            not isinstance(free_fraction, (int,float)) or
            not 0 <= free_fraction <= 0.4):
        raise ValueError("Invalid bounded GPU training input residency")
    if reuse_passes < 2 or desired_bytes == 0:
        return 0
    return max(0, min(desired_bytes, max_resident_bytes,
                      int(free_bytes * free_fraction),
                      free_bytes - minimum_compute_bytes))


def upload_cast_training_observations(destination, source, *, staging=None):
    """Upload compact observations and convert into the cached GPU dtype.

    When actor and critic both require FP32, integer board banks may be
    retained as FP32 for repeated epochs. Converting on the host first
    multiplies PCIe transfer volume by up to four; instead DMA the original
    int8/int16 tensor and perform its exact-value conversion on the GPU.
    The optional pinned staging pool owns the in-flight host buffer and
    enqueues the destination's producer-stream dependency.
    """
    if (not isinstance(destination,torch.Tensor) or
            destination.device.type!="cuda" or
            not isinstance(source,torch.Tensor) or
            source.shape!=destination.shape or
            source.requires_grad):
        raise ValueError("GPU cached conversion requires matching immutable tensors")
    if source.device.type=="cpu":
        if staging is not None:
            uploaded=staging.upload(source.contiguous())
        else:
            uploaded=source.to(destination.device)
    elif source.device==destination.device:
        uploaded=source
    else:
        uploaded=source.to(destination.device,non_blocking=True)
    destination.copy_(uploaded)
    return source.numel()*source.element_size()


def cached_training_compute_dtype(source_dtype, source_bytes, cache_budget,
                                  reuse_passes, *,
                                  exact_fp32_consumer=False,
                                  secondary_cache_budget=None):
    """Choose reusable FP32 inputs only when all originals fit in VRAM.

    Integer-encoded training observations are often converted to FP32
    separately by actor and critic every epoch. If both consumers ALWAYS
    convert integer values to FP32, int8/uint8/int16 values are exactly
    representable and a preconverted GPU bank removes those repeated cast
    kernels. Never trade full input-cache coverage for a larger dtype:
    every byte of the complete converted dataset must fit the primary
    cache budget, and optionally half the secondary peer-cache budget.
    """
    if (type(source_bytes) is not int or source_bytes < 0 or
            type(cache_budget) is not int or cache_budget < 0 or
            type(reuse_passes) is not int or reuse_passes < 0 or
            type(exact_fp32_consumer) is not bool or
            (secondary_cache_budget is not None and
             (type(secondary_cache_budget) is not int or
              secondary_cache_budget < 0))):
        raise ValueError("Invalid immutable training input dtype budget")
    if (not exact_fp32_consumer or reuse_passes < 2 or source_bytes == 0 or
            source_dtype not in (torch.int8,torch.uint8,torch.int16)):
        return None
    multiplier = 4 // torch.empty((),dtype=source_dtype).element_size()
    converted_bytes = source_bytes*multiplier
    if converted_bytes > cache_budget or (
            secondary_cache_budget is not None and
            converted_bytes > secondary_cache_budget//2):
        return None
    return torch.float32


def training_activation_workspace_budget(free_bytes, *,
                                         max_bytes=4 * 1024**3,
                                         free_fraction=0.16,
                                         minimum_other_headroom=2 * 1024**3):
    """Bound temporary GPU activations while keeping other GPU work viable.

    A batch-width tuner may explore larger per-step work within this ceiling;
    it is not an instruction to reserve or allocate the entire budget.
    The same memory-headroom rule applies to any project's backward pass
    using leased GPUs, including simultaneous inference collectors.
    """
    if (type(free_bytes) is not int or free_bytes < 0 or
            type(max_bytes) is not int or max_bytes < 1 or
            type(minimum_other_headroom) is not int or
            minimum_other_headroom < 0 or
            not isinstance(free_fraction,(int,float)) or
            not 0 <= free_fraction <= 0.5):
        raise ValueError("Invalid GPU activation workspace envelope")
    return max(0, min(int(free_bytes*free_fraction), max_bytes,
                      free_bytes-minimum_other_headroom))


def foreach_grouped_gradient_norms(named_gradients):
    """Compute per-module preclip L2 norms in one multi-tensor GPU dispatch.

    Trainable actor, critic and future project-specific modules often share
    one optimizer and global gradient clip. Reuse the same foreach result
    across their named diagnostics rather than launching one foreach pass
    for each subnetwork. The caller retains clipping/finite-check ownership.
    Nothing reads gradients onto the CPU or changes the optimizer state.
    """
    if not isinstance(named_gradients,(list,tuple)) or not named_gradients:
        raise ValueError("Expected named gradient groups")
    entries=[]
    gradients=[]
    for name,group in named_gradients:
        if not isinstance(name,str) or not name or not isinstance(group,(tuple,list)):
            raise ValueError("Invalid named gradient group")
        selected=tuple(g for g in group if g is not None)
        if any(not isinstance(g,torch.Tensor) for g in selected):
            raise ValueError("Gradient groups must contain tensors")
        entries.append((name,len(selected)))
        gradients.extend(selected)
    if not gradients or not hasattr(torch,"_foreach_norm"):
        return {}
    individual=torch._foreach_norm(gradients,2)
    grouped={}
    cursor=0
    for name,size in entries:
        if size:
            grouped[name]=torch.linalg.vector_norm(
                torch.stack(individual[cursor:cursor+size]),ord=2)
        cursor+=size
    return grouped


def stack_training_loss_diagnostics(columns, *, loss_index):
    """Keep gradients only for the actual optimizer loss column.

    A trainer commonly reports policy, value, entropy, KL and clipping
    metrics alongside the one scalar loss per board. The optimizer only
    backpropagates that loss; retaining each standalone diagnostic's
    autograd path in a stacked result unnecessarily increases backward
    graph fan-out and memory residency for every GPU microbatch.
    Preserve all numerical metric values, column positions and loss
    gradients while detaching the reporting-only columns.
    """
    if (not isinstance(columns,(tuple,list)) or
            not columns or type(loss_index) is not int or
            not 0<=loss_index<len(columns) or
            any(not isinstance(value,torch.Tensor) for value in columns)):
        raise ValueError("Training loss diagnostics require tensor columns")
    return torch.stack(tuple(
        value if i==loss_index else value.detach()
        for i,value in enumerate(columns)),dim=1)


def shared_fp32_training_input(observations, *, consumers_use_float32):
    """Convert one immutable batch ONCE for independent FP32-only networks.

    Many actor/critic pairs each perform boards.float() inside their
    forward(). An explicit exact-value consumer contract allows the caller
    to share the same FP32 tensor across both models, avoiding duplicated
    device cast kernels and allocations. This does not change gradients,
    optimizer steps, sampling, or observation values seen by those models.
    """
    if (not isinstance(observations,torch.Tensor) or
            type(consumers_use_float32) is not bool):
        raise ValueError("Shared PPO inputs require tensor and FP32 consumer contract")
    return observations.float() if consumers_use_float32 else observations


def discrete_action_logprob_entropy(logits, actions):
    """Fused-softmax-friendly discrete PPO policy terms for any action count.

    Avoid constructing a distribution and normalizing the same probabilities
    again for separate log-probability/entropy paths. This is usable from
    independent project trainers; it does not sample actions, apply clipping,
    update weights or consume the global Torch RNG.
    """
    # log_softmax is a single native CUDA/HIP reduction-plus-pointwise
    # kernel for the many small per-ant categorical distributions.
    log_probs = torch.nn.functional.log_softmax(logits, dim=-1)
    chosen_logp = log_probs.gather(
        -1, actions.unsqueeze(-1)).squeeze(-1)
    # Clamp to the finite dtype bound so masked -inf logits retain zero
    # entropy contribution instead of generating 0 * -inf = NaN.
    bounded = log_probs.clamp(min=torch.finfo(log_probs.dtype).min)
    entropy = -(log_probs.exp() * bounded).sum(dim=-1)
    return chosen_logp, entropy


_SPARSE_SPATIAL_ADMISSION = {}


def qualify_sparse_spatial_training_logits(device, *, dtype=torch.float32,
                                           actions=5, min_speedup=0.10):
    """Qualify sparse selection against dense NCHW-to-NHWC copy on one GPU.

    Uses synthetic tensors (not learner weights or Torch RNG), checks the
    exact action values and autograd gradients, and compares sustained
    forward+backward time for small and large spatial maps. Qualification
    is intended for startup, outside the useful PPO throughput window.
    The result is read-only throughout concurrent GPU training forwards.
    """
    if (not isinstance(device,torch.device) or device.type!="cuda" or
            dtype!=torch.float32 or type(actions) is not int or
            not 2<=actions<=128 or not 0<=min_speedup<=0.5):
        return False
    key=(str(device),dtype,actions)
    if key in _SPARSE_SPATIAL_ADMISSION:
        return bool(_SPARSE_SPATIAL_ADMISSION[key][1])
    # Register a disabled result before any qualification so a partial
    # ROCm backend failure cannot select an unproven optimization.
    _SPARSE_SPATIAL_ADMISSION[key]=((),frozenset())
    try:
        samples=[]
        winners=set()
        for side in (32,64):
            n=8
            area=side*side
            elements=n*actions*area
            data=torch.arange(elements,device=device,dtype=dtype)
            logits=(data*0.0007).sin().reshape(n,actions,side,side)
            logits=logits.detach().requires_grad_(True)
            count=max(1,n*area//64)
            owners=torch.arange(count,device=device,dtype=torch.int64)%n
            local=(torch.arange(count,device=device,dtype=torch.int64)*37)%area
            absolute=owners*area+local

            def dense():
                flat=logits.permute(0,2,3,1).reshape(-1,actions)
                return flat.index_select(0,absolute)

            def sparse():
                return logits.flatten(2)[owners,:,absolute-owners*area]

            def forward_backward(operation):
                value=operation()
                grad,=torch.autograd.grad(value.square().sum(),logits)
                return value.detach(),grad.detach()

            reference,ref_grad=forward_backward(dense)
            candidate,candidate_grad=forward_backward(sparse)
            if (not torch.allclose(reference,candidate,rtol=1e-6,atol=1e-6)
                    or not torch.allclose(ref_grad,candidate_grad,
                                           rtol=1e-5,atol=1e-6)):
                samples.append(elements)
                continue

            def seconds(operation):
                torch.cuda.synchronize(device)
                started=perf_counter()
                for _ in range(2):
                    forward_backward(operation)
                torch.cuda.synchronize(device)
                return (perf_counter()-started)/2

            eager_times,selected_times=[],[]
            for round_index in range(4):
                order=((dense,eager_times),(sparse,selected_times))
                if round_index%2:
                    order=order[::-1]
                for operation,timings in order:
                    timings.append(seconds(operation))
            baseline=median(eager_times)
            optimized=median(selected_times)
            samples.append(elements)
            if baseline>0 and optimized<=baseline*(1-min_speedup):
                winners.add(elements)
        _SPARSE_SPATIAL_ADMISSION[key]=(tuple(samples),frozenset(winners))
        return bool(winners)
    except (RuntimeError,ValueError,TypeError,NotImplementedError,
            torch.OutOfMemoryError):
        return False


def select_spatial_training_logits(logits, owners, absolute_cells, *,
                                   min_dense_elements=8192,
                                   max_sparse_fraction=0.125):
    """Select sparse spatial actions without copying a whole NHWC logit map.

    Conv actors produce [boards, actions, height, width]. The conventional
    permute(...).reshape(-1, actions) materializes the complete action map
    in NHWC order before index_select, even when only a few entity cells
    are used by the PPO loss. Sparse advanced indexing reads just those
    cells from the original NCHW convolution output and preserves
    autograd's gradient scatter back into the same original logits.

    Keep contiguous dense gather for tiny maps or dense populations where
    another small index kernel would not amortize removing the layout copy.
    This is generic to spatial multi-agent actor-critic trainers.
    """
    if (not isinstance(logits,torch.Tensor) or logits.ndim!=4 or
            not isinstance(owners,torch.Tensor) or
            not isinstance(absolute_cells,torch.Tensor) or
            owners.ndim!=1 or absolute_cells.ndim!=1 or
            len(owners)!=len(absolute_cells) or
            owners.device!=logits.device or
            absolute_cells.device!=logits.device or
            owners.dtype not in (torch.int32,torch.int64) or
            absolute_cells.dtype not in (torch.int32,torch.int64)):
        raise ValueError("Spatial GPU training selection requires matching indices")
    boards,actions,height,width=logits.shape
    cells_per_board=height*width
    population=len(absolute_cells)
    sizes,winners=_SPARSE_SPATIAL_ADMISSION.get(
        (str(logits.device),logits.dtype,actions),((),frozenset()))
    closest=(min(sizes,key=lambda size:abs(size-logits.numel()))
             if sizes else None)
    if (logits.device.type=="cuda" and
            logits.numel()>=min_dense_elements and
            closest in winners and
            population <= boards*cells_per_board*max_sparse_fraction):
        # Indices already carry a board owner and absolute flat cell
        # location, so derive a local location with only a small 1D GPU
        # operation. Unlike NHWC reshape, this never rearranges logits.
        local=absolute_cells-owners*cells_per_board
        return logits.flatten(2)[owners,:,local]
    dense=logits.permute(0,2,3,1).reshape(-1,actions)
    return dense.index_select(0,absolute_cells)


def gather_training_columns_once(columns, index_groups, *,
                                 max_output_bytes=16*1048576):
    """Gather immutable GPU columns once for a whole grouped minibatch.

    Shape-heterogeneous PPO workloads repeatedly select action, behavior,
    feature or metadata columns separately for every map group. A single
    concatenated index selection amortizes GPU kernel launches across all
    groups, then callers retain zero-copy views in the original group order.
    A two-column immutable value/target bank can be gathered as one 2D
    tensor rather than launching separate index kernels for each column.
    Only the temporary gathered output has an extra bounded lifetime.
    """
    if (not isinstance(columns,(tuple,list)) or not columns or
            not isinstance(index_groups,(tuple,list)) or
            len(index_groups)<2 or
            type(max_output_bytes) is not int or max_output_bytes<1):
        return None
    first=columns[0]
    if (not isinstance(first,torch.Tensor) or first.ndim not in (1,2) or
            first.device.type!="cuda" or
            any(not isinstance(column,torch.Tensor) or
                column.ndim not in (1,2) or len(column)!=len(first) or
                column.device!=first.device or column.requires_grad
                for column in columns) or
            any(not isinstance(group,torch.Tensor) or group.ndim!=1 or
                group.device!=first.device or group.requires_grad or
                group.dtype not in (torch.int32,torch.int64)
                for group in index_groups)):
        return None
    total=sum(group.numel() for group in index_groups)
    # Index groups produced as adjacent views of one packed minibatch bank
    # can be selected directly. Avoid a separate device cat allocation and
    # kernel for those groups; unrelated/non-contiguous sources keep the
    # original concatenation path and identical index order.
    contiguous=None
    head=index_groups[0]
    if total and head.is_contiguous() and head.stride(0)==1:
        storage=head.untyped_storage().data_ptr()
        offset=head.storage_offset()
        position=offset
        for group in index_groups:
            if (not group.is_contiguous() or group.stride(0)!=1 or
                    group.dtype!=head.dtype or
                    group.untyped_storage().data_ptr()!=storage or
                    group.storage_offset()!=position):
                break
            position+=group.numel()
        else:
            contiguous=torch.as_strided(head,(total,),(1,),offset)
    # Count every selected scalar, including multi-column banks. Index
    # storage is already resident for the contiguous path; otherwise
    # reserve the original bounded transient concatenation allocation.
    index_bytes=(0 if contiguous is not None else
                 total*max(group.element_size() for group in index_groups))
    output_bytes=total*sum(
        column.element_size() * (column.shape[1] if column.ndim==2 else 1)
        for column in columns)
    if not total or index_bytes+output_bytes>max_output_bytes:
        return None
    indices=(contiguous if contiguous is not None
             else torch.cat(tuple(index_groups)))
    result=tuple(column.index_select(0,indices) for column in columns)
    return result,tuple(group.numel() for group in index_groups)


def pack_immutable_gpu_scalar_columns(columns):
    """Coalesce repeated small GPU peer copies without reducing precision.

    Independent PPO advantage/old-value/target buffers often have the same
    dtype and row count. Packing their contiguous columns before the
    producer-stream event lets a secondary GPU receive all fields in ONE
    asynchronous transfer; unbind() recreates inexpensive column views.
    Return None if data types, shapes, device or immutable contract differ.
    """
    if (not isinstance(columns,(list,tuple)) or len(columns)<2 or
            any(not isinstance(value,torch.Tensor) or value.ndim!=1 or
                value.requires_grad for value in columns)):
        return None
    first = columns[0]
    if (first.device.type!='cuda' or
            any(value.device!=first.device or value.dtype!=first.dtype or
                value.shape!=first.shape for value in columns[1:])):
        return None
    return torch.stack(tuple(columns),dim=1)


@lru_cache(maxsize=32)
def _gpu_segment_backward_supported(device, dtype):
    """Check forward and autograd support once per GPU/dtype without learner state."""
    if not hasattr(torch,'segment_reduce'):
        return False
    try:
        with torch.enable_grad():
            x = torch.tensor([[1.,2.],[3.,4.],[5.,6.]],
                             device=device,dtype=dtype,requires_grad=True)
            lengths = torch.tensor([2,0,1],device=device,dtype=torch.int64)
            y = torch.segment_reduce(x,reduce='sum',lengths=lengths,axis=0)
            grad = torch.autograd.grad(y.sum(),x)[0]
            expected = x.new_tensor([[4.,6.],[0.,0.],[5.,6.]])
            return (torch.allclose(y,expected,rtol=1e-4,atol=1e-4)
                    and torch.allclose(grad,torch.ones_like(x),
                                       rtol=1e-4,atol=1e-4))
    except (RuntimeError,NotImplementedError,TypeError,AttributeError,ValueError):
        return False


# Qualified outside timed PPO work, never on a real loss graph.
_SEGMENT_THRESHOLDS = {}


def prepare_training_segment_reduction(device, *, dtype=torch.float32):
    """Admit a faster differentiable segmented reduction on a leased GPU.

    Compare the complete indexed/scatter versus segmented forward+backward
    with deterministic synthetic data at small and large entity counts.
    Retain the ordinary kernel when segmentation is unsupported or loses;
    this admission does not read a learner weight or mutate an optimizer.
    """
    if (not isinstance(device,torch.device) or device.type!='cuda' or
            not isinstance(dtype,torch.dtype)):
        raise ValueError("Expected a leased CUDA/HIP device and tensor dtype")
    key=(str(device),dtype)
    if key in _SEGMENT_THRESHOLDS:
        return _SEGMENT_THRESHOLDS[key] is not None
    _SEGMENT_THRESHOLDS[key]=None
    if not _gpu_segment_backward_supported(device,dtype):
        return False
    try:
        for rows in (2048,8192):
            groups=64
            lengths=torch.full((groups,),rows//groups,
                               device=device,dtype=torch.int64)
            owner=torch.repeat_interleave(
                torch.arange(groups,device=device,dtype=torch.int64),lengths)
            sample=torch.linspace(-1.,1.,rows*4,device=device,
                                  dtype=dtype).reshape(rows,4)
            sample=sample.detach().requires_grad_(True)

            def indexed():
                out=sample.new_zeros((groups,4))
                return out.index_add_(0,owner,sample)

            def segmented():
                return torch.segment_reduce(
                    sample,reduce='sum',lengths=lengths,axis=0)

            baseline=indexed()
            candidate=segmented()
            baseline_grad,=torch.autograd.grad(baseline.sum(),sample)
            candidate_grad,=torch.autograd.grad(candidate.sum(),sample)
            if (not torch.allclose(baseline,candidate,
                                   rtol=3e-4,atol=5e-4) or
                not torch.allclose(baseline_grad,candidate_grad,
                                   rtol=1e-5,atol=1e-5)):
                continue

            def seconds(operation):
                torch.cuda.synchronize(device)
                begun=perf_counter()
                for _ in range(2):
                    output=operation()
                    torch.autograd.grad(output.square().sum(),sample)
                torch.cuda.synchronize(device)
                return (perf_counter()-begun)/2

            ordinary,optimized=[],[]
            for repeat in range(3):
                pair=((indexed,ordinary),(segmented,optimized))
                if repeat%2:
                    pair=pair[::-1]
                for operation,measurements in pair:
                    measurements.append(seconds(operation))
            if (median(ordinary)>0 and
                median(optimized)<=median(ordinary)*.92):
                _SEGMENT_THRESHOLDS[key]=rows
                return True
    except (RuntimeError,TypeError,NotImplementedError,ValueError,
            torch.OutOfMemoryError):
        return False
    return False


def sum_consecutive_training_segments(terms, counts, owner, *,
                                      denominator_dtype, counts_tensor=None,
                                      min_parallel_rows=2048):
    """Reduce grouped training contributions without scatter atomics.

    When per-entity contributions are already packed in contiguous,
    board-owned segments, a CUDA/HIP segmented sum can avoid repeated
    atomic index_add operations into the same small number of board rows.
    The caller explicitly guarantees owner order and exact segment counts.
    For small groups or unsupported backends, retain the original
    differentiable index_add implementation and its numerical behavior.
    """
    if (not isinstance(terms,torch.Tensor) or terms.ndim!=2 or
            not isinstance(owner,torch.Tensor) or
            owner.device!=terms.device or owner.numel()!=len(terms) or
            not isinstance(counts,(list,tuple)) or
            sum(counts)!=len(terms) or any(
                not isinstance(c,Integral) or c<0 for c in counts) or
            (counts_tensor is not None and
             (not isinstance(counts_tensor,torch.Tensor) or
              counts_tensor.device!=terms.device or counts_tensor.ndim!=1 or
              counts_tensor.numel()!=len(counts) or
              counts_tensor.dtype not in (torch.int32,torch.int64) or
              counts_tensor.requires_grad))):
        raise ValueError("Training segment sizes do not match ordered contributions")
    # Record counts are immutable within a PPO minibatch. If supplied as
    # views of its existing uploaded index bank, never re-upload the same
    # tiny list on each shape group's reduction.
    threshold = _SEGMENT_THRESHOLDS.get((str(terms.device),terms.dtype))
    use_segment = (terms.device.type=='cuda' and
                   threshold is not None and
                   len(terms)>=max(min_parallel_rows,threshold))
    if use_segment:
        try:
            lengths = (counts_tensor.to(dtype=torch.int64)
                       if counts_tensor is not None else torch.as_tensor(
                           counts,device=terms.device,dtype=torch.int64))
            reduced = torch.segment_reduce(
                terms,reduce='sum',lengths=lengths,axis=0)
        except (RuntimeError,NotImplementedError,TypeError,AttributeError):
            pass
        else:
            return reduced,lengths.clamp_min(1).to(dtype=denominator_dtype)
    reduced = terms.new_zeros((len(counts),terms.shape[1]))
    reduced.index_add_(0,owner,terms)
    denominator = (counts_tensor.to(dtype=denominator_dtype)
                   if counts_tensor is not None else
                   terms.new_tensor(counts,dtype=denominator_dtype))
    return reduced,denominator.clamp_min(1)


def allocate_gpu_work_rows(rows, pixel_area, device_pixel_budgets, *, weights=None):
    """Partition same-shape training rows across independently leased GPUs.

    Return contiguous row counts, preserving observation/action order.
    A small-memory GPU receives only the rows it can safely hold; the
    optimizer's primary GPU retains any surplus. This avoids rejecting
    useful parallel work simply because an equal split would not fit.
    Optional measured forward-throughput weights bias contiguous work
    toward the less-contended GPU without ever violating memory limits.
    Returns None if fewer than two GPUs can execute the group.
    """
    if (type(rows) is not int or rows < 1 or
            type(pixel_area) is not int or pixel_area < 1 or
            not isinstance(device_pixel_budgets,(tuple,list)) or
            not 1 <= len(device_pixel_budgets) <= 16 or
            any(type(n) is not int or n < 0 for n in device_pixel_budgets) or
            (weights is not None and
             (not isinstance(weights,(tuple,list)) or
              len(weights)!=len(device_pixel_budgets) or
              any(type(value) not in (int,float) or
                  not isfinite(value) or value<=0 for value in weights)))):
        raise ValueError("Invalid bounded GPU row-partition request")
    if rows < 2 or len(device_pixel_budgets) < 2:
        return None
    capacity = [min(rows, budget//pixel_area)
                for budget in device_pixel_budgets[:min(rows,len(device_pixel_budgets))]]
    if (not capacity or capacity[0] < 1 or
            sum(capacity) < rows or sum(n>0 for n in capacity)<2):
        return None
    last = max(i for i,available in enumerate(capacity) if available > 0)
    capacity = capacity[:last+1]
    if any(n == 0 for n in capacity):
        # Keep device ordinals a prefix so PyTorch's replica and gradient
        # reduction machinery never needs sparse/non-contiguous device maps.
        return None
    remaining = rows
    result = []
    capacity_weights = (tuple(weights[:len(capacity)]) if weights is not None
                        else (1.0,)*len(capacity))
    for i,limit in enumerate(capacity):
        future = capacity[i+1:]
        # Keep at least one productive row on each admitted GPU. A share
        # must also leave enough work for the remaining devices, and meet
        # their hard physical memory ceilings.
        minimum = max(1,remaining-sum(future))
        maximum = min(limit,remaining-len(future))
        if minimum>maximum:
            return None
        ideal = round(remaining*capacity_weights[i]/
                      sum(capacity_weights[i:]))
        assigned = max(minimum,min(maximum,ideal))
        result.append(assigned)
        remaining -= assigned
    return tuple(result) if remaining == 0 and len(result) > 1 else None


class MeasuredIndependentTrainForwards:
    """Qualify overlapping GPU forwards without replaying optimizer updates.

    For two deterministic, independent differentiable functions on one CUDA/
    HIP device. They may consume the same immutable observation tensor, but
    must not share trainable parameters, mutate buffers or sample randomness.
    A training adapter owns this object across PPO updates; no weights,
    optimizer moments, actions or RNG state are stored here.

    Qualification compares extra no-grad forwards on real board shapes.
    Training forwards execute exactly once with autograd enabled, and CUDA's
    stream dependency rules retain the original gradients and reductions.
    Never assume parallel streams win for small neural networks: a measured
    10% sustained forward improvement is required per shape.
    """

    def __init__(self, first, second, device, *,
                 max_profiles=16, min_speedup=0.10,
                 qualified_independent=False,
                 first_parameters=(), second_parameters=()):
        from collections import OrderedDict
        if (not qualified_independent or not callable(first) or
                not callable(second) or not isinstance(device,torch.device) or
                device.type!="cuda" or not 1<=max_profiles<=128 or
                not 0<=min_speedup<=0.5):
            raise ValueError("GPU training forward overlap needs trusted independent functions")
        self.first,self.second,self.device = first,second,device
        first_parameters=tuple(first_parameters)
        second_parameters=tuple(second_parameters)
        # Qualification evaluates actual optimizer-relevant backward work
        # without assigning to .grad or stepping Adam. Disjoint owners
        # prevent synthetic grad propagation from crossing networks.
        if (first_parameters and second_parameters and
                not set(map(id,first_parameters)).isdisjoint(
                    set(map(id,second_parameters)))):
            raise ValueError("Overlapping GPU training forwards share gradients")
        for parameter in (*first_parameters,*second_parameters):
            if (not isinstance(parameter,torch.Tensor) or
                    parameter.device!=device or
                    not parameter.requires_grad):
                raise ValueError("Qualified GPU forward parameters mismatch device")
        self.grad_parameters=(*first_parameters,*second_parameters)
        self.min_speedup,self.max_profiles = min_speedup,max_profiles
        self.profiles,self.visits = OrderedDict(),OrderedDict()
        self.streams = None
        self.remaining_qualifications = 0
        self.calls = self.parallel_calls = self.qualifications = 0
        self.fallbacks = self.available = 0
        try:
            self.streams = (torch.cuda.Stream(device=device),
                            torch.cuda.Stream(device=device))
            self.available = 1
        except (RuntimeError,NotImplementedError,AttributeError):
            self.streams = None

    def begin_update(self, *, calibrations=1):
        if type(calibrations) is not int or not 0<=calibrations<=8:
            raise ValueError("GPU training forward qualification budget is invalid")
        self.remaining_qualifications = calibrations

    def _serial(self, first_input, second_input):
        return self.first(first_input),self.second(second_input)

    def _parallel(self, first_input, second_input):
        caller = torch.cuda.current_stream(self.device)
        left,right = self.streams
        left.wait_stream(caller)
        right.wait_stream(caller)
        with torch.cuda.stream(left):
            result_a = self.first(first_input)
            first_input.record_stream(left)
        with torch.cuda.stream(right):
            result_b = self.second(second_input)
            second_input.record_stream(right)
        caller.wait_stream(left)
        caller.wait_stream(right)
        for result in (result_a,result_b):
            for tensor in _tensor_outputs(result):
                tensor.record_stream(caller)
        return result_a,result_b

    def _time(self, action):
        caller = torch.cuda.current_stream(self.device)
        caller.synchronize()
        began = perf_counter()
        for _ in range(2):
            outputs=action()
            if self.grad_parameters:
                # Real model parameters, but no optimizer and no mutations
                # to .grad. Autograd computes the forward+backward schedule
                # on synthetic loss scalars and releases its graph.
                scalar_outputs=tuple(
                    tensor.sum() for result in outputs
                    for tensor in _tensor_outputs(result))
                torch.autograd.grad(
                    scalar_outputs,self.grad_parameters,
                    allow_unused=True,retain_graph=False)
        caller.synchronize()
        return (perf_counter()-began)/2

    def run(self, first_input, second_input):
        self.calls += 1
        if (not self.available or
                not isinstance(first_input,torch.Tensor) or
                not isinstance(second_input,torch.Tensor) or
                first_input.device!=self.device or
                second_input.device!=self.device):
            return self._serial(first_input,second_input)
        signature = ((tuple(first_input.shape),first_input.dtype),
                     (tuple(second_input.shape),second_input.dtype))
        selected = self.profiles.get(signature)
        if selected is None:
            visits = self.visits.pop(signature,0)+1
            if visits < 4 or self.remaining_qualifications == 0:
                self.visits[signature] = min(visits,4)
                if len(self.visits)>self.max_profiles*4:
                    self.visits.popitem(last=False)
                return self._serial(first_input,second_input)
            self.remaining_qualifications -= 1
            self.qualifications += 1
            selected = False
            try:
                with torch.no_grad():
                    reference = self._serial(first_input,second_input)
                    candidate = self._parallel(first_input,second_input)
                    equivalent = all(
                        torch.allclose(a,b,rtol=1e-5,atol=1e-6)
                        for a,b in zip(reference,candidate))
                if equivalent:
                    with torch.enable_grad():
                        serial = [self._time(lambda:self._serial(
                            first_input,second_input)) for _ in range(2)]
                        concurrent = [self._time(lambda:self._parallel(
                            first_input,second_input)) for _ in range(2)]
                    selected = (median(concurrent) <
                                median(serial)*(1-self.min_speedup))
            except (RuntimeError,ValueError,TypeError,NotImplementedError,
                    MemoryError,torch.OutOfMemoryError):
                self.fallbacks += 1
            self.profiles[signature] = selected
            if len(self.profiles)>self.max_profiles:
                self.profiles.popitem(last=False)
        else:
            self.profiles.move_to_end(signature)
        if selected:
            try:
                result = self._parallel(first_input,second_input)
            except (RuntimeError,ValueError,TypeError,NotImplementedError,
                    torch.OutOfMemoryError):
                self.profiles[signature] = False
                self.fallbacks += 1
            else:
                self.parallel_calls += 1
                return result
        return self._serial(first_input,second_input)

    def metrics(self):
        return dict(
            ppo_shared_train_forward_calls=self.calls,
            ppo_shared_train_forward_parallel_calls=self.parallel_calls,
            ppo_shared_train_forward_qualifications=self.qualifications,
            ppo_shared_train_forward_fallbacks=self.fallbacks,
            ppo_shared_train_forward_profiles=len(self.profiles))


class PinnedGpuStaging:
    """Bounded CPU-to-GPU upload pipeline for immutable training tensors.

    Large contiguous host fragments are copied into pinned staging banks,
    then transferred on a dedicated device stream while the CPU assembles
    later fragments. Exact destination tensor content and order are retained.
    At the ownership boundary finish() waits ONCE for all DMA to complete,
    so no pinned host storage outlives an outstanding GPU read.

    Unsupported HIP pinning or small fragments retain torch.copy_'s
    established synchronous behavior. This staging service owns no policy
    or optimizer state and does not change minibatch membership.
    """

    def __init__(self, device, *, max_pinned_bytes=64*1048576,
                 min_async_bytes=1048576):
        if (not isinstance(device,torch.device) or device.type!="cuda" or
                type(max_pinned_bytes) is not int or max_pinned_bytes<1048576 or
                type(min_async_bytes) is not int or min_async_bytes<1):
            raise ValueError("Invalid GPU staging lease or transfer threshold")
        self.device,self.max_pinned_bytes = device,max_pinned_bytes
        self.min_async_bytes = min_async_bytes
        self._copy_stream = None
        self._pending = deque()
        self._pending_bytes = 0
        self._free_pinned = []
        self._free_events = []
        self.events_allocated = self.events_reused = 0
        self._pinned_total_bytes = 0
        self._known_banks = set()
        self._closed = False
        self.async_bytes = self.direct_bytes = self.async_copies = 0
        self.pinned_reuses = self.pinned_allocations = 0
        self.pinned_cross_shape_reuses = 0

    def _make_stream(self):
        if self._copy_stream is not None:
            return True
        try:
            self._copy_stream = torch.cuda.Stream(device=self.device)
        except (RuntimeError,NotImplementedError,AttributeError):
            return False
        return True

    def _reap_completed(self):
        # GPU completion determines when a pinned host bank may be written
        # again. Querying an event does not synchronize CPU or GPU work.
        while self._pending:
            pinned,event,size = self._pending[0]
            try:
                completed = event.query()
            except (RuntimeError,NotImplementedError):
                completed = False
            if not completed:
                break
            self._pending.popleft()
            self._pending_bytes -= size
            self._free_pinned.append(pinned)
            self._free_events.append(event)

    def _checkout(self, source, *, size_class=False):
        self._reap_completed()
        wanted=source.numel()
        element_bytes=source.element_size()
        # Consecutive shuffled PPO windows rarely contain exactly the same
        # number of ant/index rows. Reuse a power-of-two pinned capacity
        # for these one-dimensional integer banks, not a fresh allocation
        # for each different length. Other inputs keep exact-shape reuse.
        capacity=(1<<(wanted-1).bit_length()
                  if size_class and wanted else wanted)
        if capacity*element_bytes>self.max_pinned_bytes//2:
            capacity=wanted
        def fits(buffer):
            return (buffer.dtype==source.dtype and
                    (buffer.numel()>=wanted if size_class
                     else buffer.numel()==wanted))
        def reuse(buffer):
            self.pinned_reuses += 1
            if buffer.shape!=source.shape:
                self.pinned_cross_shape_reuses += 1
            return buffer

        eligible=[i for i,b in enumerate(self._free_pinned) if fits(b)]
        if eligible:
            index=min(eligible,key=lambda i:self._free_pinned[i].numel())
            return reuse(self._free_pinned.pop(index))
        nbytes=capacity*element_bytes
        while self._pinned_total_bytes+nbytes > self.max_pinned_bytes:
            if self._free_pinned:
                old = self._free_pinned.pop()
                self._pinned_total_bytes -= old.numel()*old.element_size()
                continue
            if not self._pending:
                return None
            # All DMA reads use one stream. A completed oldest allocation
            # may be reused, including a different-sized index-bank view.
            pinned,event,size = self._pending.popleft()
            event.synchronize()
            self._pending_bytes -= size
            self._free_events.append(event)
            if fits(pinned):
                return reuse(pinned)
            self._pinned_total_bytes -= size
        try:
            pinned = torch.empty(
                (capacity,) if size_class else source.shape,
                dtype=source.dtype,device='cpu',pin_memory=True)
        except (RuntimeError,NotImplementedError,MemoryError):
            return None
        self._pinned_total_bytes += nbytes
        self.pinned_allocations += 1
        return pinned

    def copy_(self, destination, source, *, min_async_bytes=None):
        if (min_async_bytes is not None and
                (type(min_async_bytes) is not int or min_async_bytes<1)):
            raise ValueError("Invalid pinned upload threshold")
        threshold=(self.min_async_bytes if min_async_bytes is None
                   else min_async_bytes)
        if (self._closed or not isinstance(destination,torch.Tensor) or
                destination.device!=self.device or
                not isinstance(source,torch.Tensor) or
                source.device.type!="cpu" or
                source.shape!=destination.shape or
                source.dtype!=destination.dtype):
            raise ValueError("GPU staging requires matching leased output and CPU input")
        nbytes = source.numel()*source.element_size()
        if (not source.is_contiguous() or
                nbytes < threshold or
                nbytes > self.max_pinned_bytes//2 or
                not self._make_stream()):
            destination.copy_(source)
            self.direct_bytes += nbytes
            return destination
        backing = self._checkout(source,size_class=(
            min_async_bytes is not None and source.ndim==1 and
            source.dtype in (torch.int32,torch.int64)))
        if backing is None:
            destination.copy_(source)
            self.direct_bytes += nbytes
            return destination
        pinned=backing.view(-1).narrow(
            0,0,source.numel()).view(source.shape)
        try:
            pinned.copy_(source)
        except (RuntimeError,NotImplementedError):
            self._free_pinned.append(backing)
            destination.copy_(source)
            self.direct_bytes += nbytes
            return destination
        # A newly allocated destination may belong to a stream different
        # from the copy stream. Establish allocation/use order once per
        # bank, without synchronizing the host or imposing a GPU barrier.
        identity = destination.untyped_storage().data_ptr()
        if identity not in self._known_banks:
            self._copy_stream.wait_stream(torch.cuda.current_stream(self.device))
            self._known_banks.add(identity)
        event = None
        try:
            with torch.cuda.stream(self._copy_stream):
                destination.copy_(pinned,non_blocking=True)
                if self._free_events:
                    event = self._free_events.pop()
                    self.events_reused += 1
                else:
                    event = torch.cuda.Event()
                    self.events_allocated += 1
                event.record(self._copy_stream)
            destination.record_stream(self._copy_stream)
        except (RuntimeError,NotImplementedError):
            # A partially submitted DMA must finish before its host
            # staging buffer is released or rewritten by another request.
            self._copy_stream.synchronize()
            if event is not None:
                self._free_events.append(event)
            self._free_pinned.append(backing)
            destination.copy_(source)
            self.direct_bytes += nbytes
            return destination
        # Charge the complete backing allocation to the pool lease until
        # its DMA finishes. Only the requested prefix is transferred.
        backing_bytes=backing.numel()*backing.element_size()
        self._pending.append((backing,event,backing_bytes))
        self._pending_bytes += backing_bytes
        self.async_bytes += nbytes
        self.async_copies += 1
        return destination

    def upload(self, source, *, min_async_bytes=None):
        """Transfer a transient PPO microbatch through the same pinned pool.

        Output allocation remains on the caller's compute stream. When a
        pinned DMA is selected, only that stream waits on the copy's event;
        no CPU-wide device sync is introduced. Host buffers remain owned by
        this pool until the device event completes and can then be reused by
        subsequent microbatches or a later PPO update.
        """
        if (self._closed or not isinstance(source,torch.Tensor) or
                source.device.type!="cpu" or not source.is_contiguous()):
            raise ValueError("GPU upload requires contiguous host input and a live lease")
        consumer = torch.cuda.current_stream(self.device)
        destination = torch.empty(
            source.shape,dtype=source.dtype,device=self.device)
        previous_async = self.async_copies
        # Transient uploads have no stable bank lifetime. The allocator
        # may recycle a previously uploaded address after compute uses
        # it, so a remembered pointer must never bypass the producer
        # stream dependency for the new allocation.
        identity = destination.untyped_storage().data_ptr()
        self._known_banks.discard(identity)
        try:
            self.copy_(destination,source,min_async_bytes=min_async_bytes)
        finally:
            self._known_banks.discard(identity)
        if self.async_copies != previous_async:
            # copy_ recorded an event on the same leased transfer stream.
            # Do not wait on its completion on the host. The stream wait
            # makes the tensor safe for immediate GPU model consumption.
            event = self._pending[-1][1]
            consumer.wait_event(event)
            destination.record_stream(consumer)
        return destination

    def upload_indices(self, source):
        """Avoid blocking compute on small, consolidated PPO index banks.

        Index banks are immutable, often below the observation transfer
        threshold, and consumed immediately by gathers. A 16 KiB floor
        avoids a separate stream/event round trip for tiny scalar copies;
        larger banks retain the same bounded pinned lifetime and fallback
        behavior as observation uploads.
        """
        if (not isinstance(source,torch.Tensor) or source.ndim!=1 or
                source.dtype not in (torch.int32,torch.int64)):
            raise ValueError("GPU index upload requires a flat integer bank")
        return self.upload(source,min_async_bytes=16*1024)

    def finish_update(self):
        """Finish one immutable GPU upload generation, retaining host buffers.

        Source tensors, shape banks and observations change each PPO update,
        but the pinned host *capacity* and copy stream can be reused. Drain
        all outstanding reads before a new rollout can overwrite any data.
        One stream synchronization per immutable upload generation replaces
        many per-chunk host waits; no model/optimizer state survives here.
        """
        if self._closed:
            return
        if self._copy_stream is not None:
            self._copy_stream.synchronize()
        while self._pending:
            pinned,event,size = self._pending.popleft()
            self._pending_bytes -= size
            self._free_pinned.append(pinned)
            self._free_events.append(event)
        self._pending_bytes = 0
        self._known_banks.clear()

    @contextmanager
    def update(self):
        if self._closed:
            raise RuntimeError("GPU pinned staging pool has been closed")
        try:
            yield self
        finally:
            self.finish_update()

    def finish(self):
        if self._closed:
            return
        self.finish_update()
        self._closed = True
        self._free_pinned.clear()
        self._free_events.clear()
        self._pinned_total_bytes = 0
        self._copy_stream = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.finish()


class ImmutableGpuScalarBank:
    """Cache complete immutable training scalar columns once on a peer GPU.

    PPO's advantage, old-value and target arrays do not change across
    optimization epochs. Unlike a microbatch transfer cache, this compact
    bank stores each logical record exactly once, then gathers arbitrary
    reshuffled minibatches locally on the destination device. Ownership,
    record keys, and the GPU memory lease remain with the calling trainer.
    """

    def __init__(self, device, *, max_bytes=64*1048576):
        if (not isinstance(device,torch.device) or device.type!="cuda" or
                type(max_bytes) is not int or max_bytes<1):
            raise ValueError("Invalid secondary GPU scalar residency lease")
        self.device,self.max_bytes=device,max_bytes
        self._indices={}
        self._values=None
        self._ready=None
        self._source=None
        self.rows=self.saved_bytes=self.gather_calls=0

    def stage(self, record_keys, columns, *, packed=None):
        if (not isinstance(record_keys,(list,tuple)) or
                not isinstance(columns,(list,tuple)) or len(columns)<2 or
                not all(isinstance(t,torch.Tensor) and t.ndim==1
                        for t in columns) or
                any(len(t)!=len(record_keys) or t.requires_grad
                    for t in columns) or
                len(set(record_keys))!=len(record_keys)):
            return False
        first=columns[0]
        if (first.device.type!="cuda" or first.device==self.device or
                any(t.device!=first.device or t.dtype!=first.dtype
                    for t in columns)):
            return False
        wanted=len(record_keys)*len(columns)*first.element_size()
        if wanted>self.max_bytes:
            return False
        if packed is not None and (
                not isinstance(packed,torch.Tensor) or packed.ndim!=2 or
                packed.shape!=(len(record_keys),len(columns)) or
                packed.device!=first.device or packed.dtype!=first.dtype or
                not packed.is_contiguous() or packed.requires_grad):
            return False
        try:
            # The learner already keeps one immutable packed scalar bank
            # for its own shape-group gathers. Reuse it as the peer-DMA
            # source instead of launching a second full-rollout torch.stack.
            # Column identity/order is the trusted trainer's contract.
            combined=(packed if packed is not None else
                      torch.stack(columns,dim=1))
            producer=torch.cuda.current_stream(first.device)
            # The copy is enqueued on the DESTINATION device's current
            # stream, which may differ from the stream later chosen by its
            # persistent GPU worker. Ensure that the source GPU has finished
            # building the immutable tensor before scheduling peer DMA.
            destination_stream=torch.cuda.current_stream(self.device)
            try:
                destination_stream.wait_stream(producer)
            except (RuntimeError,NotImplementedError):
                # Some ROCm versions cannot enqueue cross-GPU event waits.
                # A one-time staged dataset may wait for its producer;
                # avoid introducing this barrier for each minibatch gather.
                producer.synchronize()
            with torch.cuda.device(self.device), torch.cuda.stream(destination_stream):
                remote=combined.to(self.device,non_blocking=True)
                ready=torch.cuda.Event()
                ready.record(destination_stream)
        except (RuntimeError,MemoryError,torch.OutOfMemoryError):
            return False
        self._indices={key:i for i,key in enumerate(record_keys)}
        self._values=remote
        self._source=combined
        self._ready=ready
        self.rows=len(record_keys)
        return True

    def gather(self, record_keys):
        if self._values is None:
            return None
        try:
            positions=[self._indices[key] for key in record_keys]
        except KeyError:
            return None
        stream=torch.cuda.current_stream(self.device)
        if self._ready is not None:
            try:
                stream.wait_event(self._ready)
            except (RuntimeError,NotImplementedError):
                # Unsupported cross-device HIP events are uncommon; one
                # admission-boundary host wait is safer than stale scalar
                # inputs, and never repeats after the first gather.
                self._ready.synchronize()
            self._ready=None
        indices=torch.as_tensor(positions,device=self.device,dtype=torch.int64)
        result=self._values.index_select(0,indices)
        self.saved_bytes += result.numel()*result.element_size()
        self.gather_calls += 1
        return tuple(result.unbind(1))

    def clear(self):
        self._indices.clear()
        self._values=self._ready=self._source=None
        self.rows=0


class ImmutableGpuRowCache:
    """Reuse immutable observation rows already transferred to another GPU.

    The caller supplies stable per-update row identities; no global input
    state, epoch order, optimizer or model state is owned here. A first
    transfer is the normal nonblocking device copy. Later groups containing
    only cached rows are reassembled on the destination GPU, avoiding
    repeated PCIe/peer copies. The cache is bounded by a caller-owned GPU
    memory lease and must be cleared at the end of the training update.
    """

    def __init__(self, device, *, max_bytes):
        if (not isinstance(device,torch.device) or device.type!="cuda" or
                type(max_bytes) is not int or max_bytes<0):
            raise ValueError("Invalid leased GPU row-cache device or budget")
        self.device,self.max_bytes = device,max_bytes
        self.rows = {}
        self.resident_bytes = self.hits = self.misses = self.bytes_saved = 0
        self.copied_bytes = 0
        self.admitted_buffers = 0
        self.oom_evictions = 0
        self.contiguous_view_hits = 0
        self.partial_hits = 0
        self.partial_bytes_saved = 0
        self._seed_ready = None
        self._seed_sources = []
        self.seeded_rows = self.seeded_bytes = 0

    def seed_bank(self, identities, source):
        """Bulk-stage one frequently reused immutable shape bank.

        The source is already a contiguous GPU bank belonging to the
        primary optimizer. Reserve only a bounded portion of the secondary
        cache and keep each row's identity; subsequent shuffled minibatches
        read local GPU views. This runs before the PPO optimizer loop, not
        during a critical forward/backward dispatch.
        """
        if (not isinstance(identities,(tuple,list)) or
                not isinstance(source,torch.Tensor) or
                source.device.type!="cuda" or source.device==self.device or
                source.ndim<2 or not source.is_contiguous() or
                len(identities)!=len(source) or
                len(set(identities))!=len(identities) or
                any(key in self.rows for key in identities)):
            return 0
        bytes_needed=source.numel()*source.element_size()
        if not bytes_needed or self.resident_bytes+bytes_needed>self.max_bytes:
            return 0
        try:
            producer=torch.cuda.current_stream(source.device)
            consumer=torch.cuda.current_stream(self.device)
            try:
                consumer.wait_stream(producer)
            except (RuntimeError,NotImplementedError):
                producer.synchronize()
            with torch.cuda.device(self.device), torch.cuda.stream(consumer):
                remote=source.to(self.device,non_blocking=True)
                ready=torch.cuda.Event()
                ready.record(consumer)
        except (RuntimeError,NotImplementedError,torch.OutOfMemoryError):
            return 0
        for index,key in enumerate(identities):
            self.rows[key]=remote[index:index+1]
        self._seed_ready=ready
        self._seed_sources.append(source)
        self.seeded_rows += len(identities)
        self.seeded_bytes += bytes_needed
        self.resident_bytes += bytes_needed
        self.copied_bytes += bytes_needed
        self.admitted_buffers += 1
        return len(identities)

    def _wait_seed(self):
        if self._seed_ready is None:
            return
        try:
            torch.cuda.current_stream(self.device).wait_event(self._seed_ready)
        except (RuntimeError,NotImplementedError):
            self._seed_ready.synchronize()
        self._seed_ready=None
        self._seed_sources.clear()

    @staticmethod
    def _contiguous_cached_view(views):
        """Reuse a contiguous original GPU allocation without a cat kernel."""
        first = views[0]
        stride = first.stride()
        start = first.storage_offset()
        storage = first.untyped_storage().data_ptr()
        step = stride[0]
        if all(row.shape == first.shape and row.stride() == stride and
               row.untyped_storage().data_ptr() == storage and
               row.storage_offset() == start + i*step
               for i,row in enumerate(views)):
            return torch.as_strided(
                first,(len(views),*first.shape[1:]),stride)
        return None

    def fetch(self, identifiers, boards, *, producer_stream=None):
        if (not isinstance(boards,torch.Tensor) or boards.ndim<1 or
                boards.device.type!="cuda" or
                len(identifiers)!=len(boards) or
                len(set(identifiers))!=len(identifiers)):
            raise ValueError("Immutable GPU row request changed shape or identity")
        if boards.device == self.device:
            return boards
        if self._seed_ready is not None:
            self._wait_seed()
        keys = tuple(identifiers)
        if keys and all(key in self.rows and
                        self.rows[key].shape[1:] == boards.shape[1:] and
                        self.rows[key].dtype == boards.dtype for key in keys):
            try:
                if len(keys)==1:
                    result = self.rows[keys[0]]
                else:
                    views = [self.rows[key] for key in keys]
                    result = self._contiguous_cached_view(views)
                    if result is None:
                        result = torch.cat(views,dim=0)
                    else:
                        self.contiguous_view_hits += 1
            except torch.OutOfMemoryError:
                # Optional caching cannot exhaust the remaining activation
                # workspace and prevent a valid PPO minibatch. Evict the
                # secondary input bank and restore ordinary peer copying.
                self.oom_evictions += 1
                self.clear()
            else:
                self.hits += len(keys)
                self.bytes_saved += boards.numel()*boards.element_size()
                return result
        # Most PPO epochs reshuffle the same immutable observations, so
        # groups frequently contain both previously copied and new rows.
        # Reassemble on the destination only when the saved peer transfer
        # is large enough to amortize extra index/cat kernels. Sparse or
        # tiny batches retain the original direct-copy path.
        cached = [
            key in self.rows and
            self.rows[key].shape[1:] == boards.shape[1:] and
            self.rows[key].dtype == boards.dtype
            for key in keys
        ]
        hit_indices = [index for index,present in enumerate(cached) if present]
        missing_indices = [index for index,present in enumerate(cached) if not present]
        row_bytes = (boards[0].numel()*boards.element_size()
                     if len(boards) else 0)
        if (hit_indices and missing_indices and
                len(hit_indices)*2 >= len(keys) and
                len(hit_indices)*row_bytes >= 512*1024):
            try:
                # One compact source gather and one peer transfer replace
                # the entire batch's data movement. The gather must run on
                # the SOURCE GPU's producer stream, even when this call is
                # launched by a secondary GPU's persistent Python worker.
                # Otherwise per-thread default streams can race with the
                # primary worker producing the observation tensor.
                if producer_stream is None:
                    source_rows = boards.index_select(
                        0,torch.as_tensor(missing_indices,device=boards.device))
                else:
                    with torch.cuda.device(boards.device), torch.cuda.stream(producer_stream):
                        source_rows = boards.index_select(
                            0,torch.as_tensor(missing_indices,device=boards.device))
                        ready = torch.cuda.Event()
                        ready.record(producer_stream)
                    torch.cuda.current_stream(self.device).wait_event(ready)
                fresh = source_rows.to(self.device,non_blocking=True)
                result = torch.empty(
                    boards.shape,device=self.device,dtype=boards.dtype)
                prior = torch.cat(
                    [self.rows[keys[i]] for i in hit_indices],dim=0)
                result.index_copy_(
                    0,torch.as_tensor(hit_indices,device=self.device),prior)
                result.index_copy_(
                    0,torch.as_tensor(missing_indices,device=self.device),fresh)
            except torch.OutOfMemoryError:
                # This is an optional optimization. Discard cached
                # residency rather than prevent the required PPO forward.
                self.oom_evictions += 1
                self.clear()
                # Release temporary output/selection buffers before the
                # authoritative full peer copy attempts its allocation.
                source_rows = fresh = result = prior = None
            else:
                fresh_bytes = fresh.numel()*fresh.element_size()
                self.copied_bytes += fresh_bytes
                self.hits += len(hit_indices)
                self.partial_hits += 1
                self.bytes_saved += len(hit_indices)*row_bytes
                self.partial_bytes_saved += len(hit_indices)*row_bytes
                if self.resident_bytes+fresh_bytes <= self.max_bytes:
                    for index,key in enumerate(missing_indices):
                        self.rows[keys[key]] = fresh[index:index+1]
                    self.resident_bytes += fresh_bytes
                    self.admitted_buffers += 1
                return result
        self.misses += 1
        try:
            copied = boards.to(self.device,non_blocking=True)
        except torch.OutOfMemoryError:
            if not self.rows:
                raise
            self.oom_evictions += 1
            self.clear()
            copied = boards.to(self.device,non_blocking=True)
        size = copied.numel()*copied.element_size()
        self.copied_bytes += size
        # Store new rows as views of one already-necessary device transfer.
        # Charging the entire underlying storage prevents the retained
        # slices from quietly exceeding the lease if a batch contains
        # both cached and uncached rows.
        if (self.resident_bytes+size <= self.max_bytes and
                any(key not in self.rows for key in keys)):
            for index,key in enumerate(keys):
                if key not in self.rows:
                    self.rows[key] = copied[index:index+1]
            self.resident_bytes += size
            self.admitted_buffers += 1
        return copied

    def clear(self):
        # Retired peer banks must not be reclaimed while an asynchronous
        # seed copy is still producing their contents.
        if self._seed_ready is not None:
            self._seed_ready.synchronize()
            self._seed_ready=None
        self._seed_sources.clear()
        self.rows.clear()
        self.resident_bytes = 0

    def metrics(self):
        return dict(
            cached_peer_rows=self.hits,
            cached_peer_bytes_saved=self.bytes_saved,
            cached_peer_resident_bytes=self.resident_bytes,
            cached_peer_copied_bytes=self.copied_bytes,
            cached_peer_buffer_allocations=self.admitted_buffers,
            cached_peer_oom_evictions=self.oom_evictions,
            cached_peer_seeded_rows=self.seeded_rows,
            cached_peer_seeded_bytes=self.seeded_bytes,
            cached_peer_contiguous_view_hits=self.contiguous_view_hits,
            cached_peer_partial_hits=self.partial_hits,
            cached_peer_partial_bytes_saved=self.partial_bytes_saved)


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
            state_tensors = tuple(_state_tensors(optimizer.state))
            # Within one optimizer, weight views or repeated references to
            # the same state buffer are safe. Reject only storage shared
            # ACROSS independent learners executing on separate streams.
            current_owner = set()
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
                    current_owner.add(key)
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
                    current_owner.add(key)
            identities.update(current_owner)
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
        # GPU event rates are separate from end-to-end wall rates. The
        # latter select the fastest learner strategy, while event-only
        # rates are the sole valid baseline for steady-state GPU drift.
        self.calibration_gpu_rates = {n:[] for n in self.probe_widths}
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
        # All inputs are ready on the caller's stream before this call.
        # A stream needs one wait edge per batch, not per independent
        # small model. The stream's own queue orders later model steps.
        for stream in self.streams[:width]:
            stream.wait_stream(caller)
        result = []
        for index, (step, tensors) in enumerate(zip(steps, inputs)):
            stream = self.streams[index % width]
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
        baseline_rates = self.calibration_gpu_rates.get(self.selected_streams,[])
        baseline = median(baseline_rates) if baseline_rates else 0
        if (baseline > 0 and (measured < baseline*(1-self.retune_threshold) or
                              measured > baseline*(1+self.retune_threshold))):
            # Re-qualify using future distinct optimizer updates. No
            # captured update is ever rerun, reset, or checkpoint-replayed.
            self._retunes += 1
            self.serial_rates.clear()
            for rates in self.parallel_rates.values():
                rates.clear()
            for rates in self.calibration_gpu_rates.values():
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
        calibrated_events = None
        if measuring and self._sampling_enabled:
            try:
                start_event = torch.cuda.Event(enable_timing=True)
                end_event = torch.cuda.Event(enable_timing=True)
                start_event.record(torch.cuda.current_stream(self.device))
                calibrated_events = (start_event,end_event)
            except (RuntimeError, NotImplementedError):
                self._sampling_enabled = False
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
        if calibrated_events is not None:
            try:
                calibrated_events[1].record(torch.cuda.current_stream(self.device))
            except (RuntimeError, NotImplementedError):
                self._sampling_enabled = False
                calibrated_events = None
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
            if calibrated_events is not None:
                try:
                    gpu_seconds = calibrated_events[0].elapsed_time(
                        calibrated_events[1])/1000.0
                    if gpu_seconds>0:
                        self.calibration_gpu_rates[width].append(count/gpu_seconds)
                except (RuntimeError, NotImplementedError):
                    self._sampling_enabled = False
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
            gpu_training_gpu_reference_samples=sum(
                map(len,self.calibration_gpu_rates.values())),
            gpu_training_calibration_complete=int(self.phase == "settled"),
            gpu_training_last_qualified_seconds=self.last_qualified_seconds,
            gpu_training_qualified_work_per_second=self.last_qualified_work_rate,
        )


class PersistentGpuForwardPool:
    """Reuse one Python dispatch worker per leased GPU for autograd forwards.

    This is an optional host-launch optimization for an existing data-
    parallel trainer. It neither broadcasts models nor reduces gradients:
    the caller owns those operations and the model/optimizer lifetimes.
    Unlike torch.nn.parallel.parallel_apply, worker threads are created
    once and reused for every shape group in the same training update.

    Each call receives the caller's current device streams, grad mode and
    CUDA/HIP autocast state. All submitted jobs finish before returning or
    propagating a model error, so a subsequent optimizer step never races
    unfinished Python dispatch. This object is single-owner, not reentrant.
    """

    def __init__(self, devices):
        if (not isinstance(devices, (tuple, list)) or
                len(devices) < 2 or len(devices) > 16 or
                any(type(d) is not int or d < 0 for d in devices) or
                len(set(devices)) != len(devices) or
                not torch.cuda.is_available()):
            raise ValueError("GPU forward pool needs 2–16 distinct leased ordinals")
        for device in devices:
            torch.cuda.get_device_properties(device)
        self.devices = tuple(devices)
        self._owner = current_thread()
        self._workers = ThreadPoolExecutor(
            max_workers=len(devices), thread_name_prefix="hub-gpu-forward")
        self._closed = False
        self.calls = 0
        self.prepared_calls = 0
        self.owner_inline_prepared_calls = 0
        self.owner_inline_direct_calls = 0
        # This pool is single-owner and every prepared call joins all
        # worker submissions before returning. One producer event can be
        # recorded repeatedly on the same primary stream; every queued
        # peer wait observes the event generation at wait submission.
        self._producer_event = torch.cuda.Event()
        self._producer_event_records = 0
        self._pending_device_timings = deque()
        self._device_work_rates = {device:deque(maxlen=8)
                                   for device in self.devices}
        # Work-rate ratios require identical shapes and balanced work.
        # Raw per-GPU forward timing can include heterogeneous map pairs,
        # but must never be fed back into the capacity weight allocator.
        self._device_forward_ms = {device:deque(maxlen=16)
                                   for device in self.devices}
        self.device_forward_timing_samples = 0
        self.device_timing_samples = 0
        self._admitted_capacity_weights = {}
        self.training_updates = 0

    def begin_training_update(self):
        """Requalify compute sharing after on-policy inputs are replaced.

        The machine's independent background GPU work can change from one
        PPO update to the next. Retain the expensive Python workers, but
        discard per-update throughput weights and timing evidence. The
        caller must have joined the previous update's backward/optimizer
        work before this boundary.
        """
        if self._closed or current_thread() is not self._owner:
            raise RuntimeError("GPU forward update requires its owner")
        # Last update's event samples have completed at the existing PPO
        # GPU synchronization boundary. Preserve a bounded recent window:
        # short on-policy updates may not contain three samples on their
        # own, and clearing every update prevented adaptation entirely.
        self._collect_device_timings()
        # A slow secondary card may finish its forward timing event after
        # GPU 0's PPO completion boundary. Keep the bounded unfinished
        # events so the next update can learn its true service rate rather
        # than silently discarding the slower card's samples.
        self._admitted_capacity_weights.clear()
        self.training_updates += 1
        if self.training_updates % 8 == 0:
            # Periodically remeasure under the current co-tenant load,
            # rather than treating last hour's GPU sharing as permanent.
            for rates in self._device_work_rates.values():
                rates.clear()
            for timings in self._device_forward_ms.values():
                timings.clear()

    def _collect_device_timings(self):
        if not self._pending_device_timings:
            return
        waiting = deque()
        while self._pending_device_timings:
            device,begin,end,pixels = self._pending_device_timings.popleft()
            try:
                if not end.query():
                    waiting.append((device,begin,end,pixels))
                    continue
                elapsed_ms = begin.elapsed_time(end)
            except (RuntimeError,ValueError,NotImplementedError):
                continue
            if elapsed_ms > 0:
                self._device_forward_ms[device].append(elapsed_ms)
                self.device_forward_timing_samples += 1
                if pixels > 0:
                    # Only shape-matched, balanced measurements can
                    # change assignment weights. Mixed maps are observable
                    # but cannot falsely teach one GPU to take more work.
                    self._device_work_rates[device].append(pixels/elapsed_ms)
                    self.device_timing_samples += 1
        self._pending_device_timings = waiting

    def measured_capacity_weights(self, devices=None):
        """Return stable relative effective pixel rates, without GPU waits.

        This is advisory. The existing memory allocator remains the hard
        per-device upper bound, and the outer useful-throughput tuner can
        still choose one GPU when data parallelism loses overall.
        """
        self._collect_device_timings()
        ordinals = self.devices if devices is None else tuple(devices)
        if ordinals in self._admitted_capacity_weights:
            return self._admitted_capacity_weights[ordinals]
        if any(len(self._device_work_rates[device])<3 for device in ordinals):
            return None
        rates = [median(self._device_work_rates[device]) for device in ordinals]
        if min(rates) <= 0 or max(rates)/min(rates)<1.15:
            return None
        reference = median(rates)
        # A rate derived from equal shares is NOT a stable measurement
        # after assigning unequal shares: fixed GPU launch overhead makes
        # pixels/ms depend on batch size. Freeze this PPO update's initial
        # comparable split, rather than creating self-reinforcing imbalance
        # and starving the slower card as the allocated rows shrink.
        chosen = tuple(min(1.5,max(0.67,rate/reference)) for rate in rates)
        self._admitted_capacity_weights[ordinals] = chosen
        return chosen

    def capacity_metrics(self):
        """Read completed per-GPU forward service rates without a GPU wait.

        Timing samples cover input materialization and model forward only,
        not PPO backward, optimizer steps, or simulated frames. Callers
        can compare the two devices' effective rates under their shared
        workload and decide whether peer contention is worth addressing.
        Values are reported only after three completed samples per device;
        an absent rate is unknown, not a measured zero.
        """
        if self._closed or current_thread() is not self._owner:
            raise RuntimeError("GPU forward metrics require the live owner")
        self._collect_device_timings()
        metrics = dict(
            gpu_forward_capacity_pending_events=len(self._pending_device_timings),
            gpu_forward_capacity_timing_samples=self.device_timing_samples,
            gpu_forward_all_shape_timing_samples=self.device_forward_timing_samples,
            gpu_forward_primary_inline_dispatches=self.owner_inline_prepared_calls,
            gpu_forward_direct_inline_dispatches=self.owner_inline_direct_calls,
            gpu_forward_producer_event_reuses=max(
                0,self._producer_event_records-1))
        rates = []
        for slot, device in enumerate(self.devices):
            # Heterogeneous-map forward durations are NOT directly
            # comparable across cards. Expose each card's activity while
            # reserving capacity ratios for same-shape balanced pairs.
            durations = self._device_forward_ms[device]
            metrics[f'gpu_forward_device_{slot}_timed_forwards'] = len(durations)
            if durations:
                metrics[f'gpu_forward_device_{slot}_median_forward_ms'] = (
                    median(durations))
            samples = self._device_work_rates[device]
            metrics[f'gpu_forward_capacity_device_{slot}_samples'] = len(samples)
            rate = median(samples) if len(samples) >= 3 else None
            rates.append(rate)
            if rate is not None:
                metrics[f'gpu_forward_capacity_device_{slot}_pixels_per_ms'] = rate
        if len(rates) >= 2 and rates[0] is not None and rates[1] is not None:
            metrics['gpu_forward_capacity_secondary_primary_rate_ratio'] = (
                rates[1] / rates[0])
        return metrics

    @staticmethod
    def _forward(model, parts, device, stream, grad_enabled,
                 autocast_enabled, autocast_dtype):
        with (torch.set_grad_enabled(grad_enabled),
              torch.cuda.device(device),
              torch.cuda.stream(stream),
              torch.autocast("cuda", enabled=autocast_enabled,
                             dtype=autocast_dtype)):
            return model(*parts)

    def run(self, models, inputs, devices=None):
        if self._closed or current_thread() is not self._owner:
            raise RuntimeError("GPU forward dispatch requires its live owner thread")
        ordinals = self.devices if devices is None else tuple(devices)
        if (not 1 <= len(ordinals) <= len(self.devices) or
                ordinals != self.devices[:len(ordinals)] or
                len(models) != len(inputs) or len(models) != len(ordinals)):
            raise ValueError("Forward cohort does not match the reserved GPUs")
        streams = [torch.cuda.current_stream(d) for d in ordinals]
        grad_enabled = torch.is_grad_enabled()
        autocast_enabled = torch.is_autocast_enabled("cuda")
        autocast_dtype = torch.get_autocast_dtype("cuda")
        if len(ordinals)==2:
            # The learner owner can enqueue its own GPU work while GPU 1's
            # persistent worker runs concurrently. Do not submit and then
            # block waiting for a second worker just to launch GPU 0.
            peer=self._workers.submit(
                self._forward,models[1],inputs[1],ordinals[1],streams[1],
                grad_enabled,autocast_enabled,autocast_dtype)
            try:
                primary=self._forward(
                    models[0],inputs[0],ordinals[0],streams[0],
                    grad_enabled,autocast_enabled,autocast_dtype)
            except BaseException:
                wait((peer,))
                raise
            outputs=[primary,peer.result()]
            self.owner_inline_direct_calls += 1
        else:
            futures = [self._workers.submit(
                self._forward, model, parts, device, stream,
                grad_enabled, autocast_enabled, autocast_dtype)
                for model,parts,device,stream in zip(
                    models,inputs,ordinals,streams)]
            # Join every dispatched GPU even if one model failed.
            wait(futures)
            outputs=[future.result() for future in futures]
        self.calls += 1
        return outputs

    @staticmethod
    def _prepared_forward(model, factory, device, stream, producer_event,
                          grad_enabled, autocast_enabled, autocast_dtype,
                          sampled_pixels):
        with (torch.set_grad_enabled(grad_enabled),
              torch.cuda.device(device),
              torch.cuda.stream(stream),
              torch.autocast("cuda", enabled=autocast_enabled,
                             dtype=autocast_dtype)):
            started = finished = None
            # Per-device input factories may copy tensors produced on GPU 0
            # during the current optimizer minibatch. The producer event
            # establishes a proper cross-device dependency without waiting
            # for GPU 2's transfer on the host or delaying GPU 1's forward.
            if producer_event is not None:
                try:
                    stream.wait_event(producer_event)
                except (RuntimeError,NotImplementedError):
                    # Some HIP configurations cannot enqueue cross-device
                    # event waits. Block only this worker, not GPU 1's
                    # independent forward, while preserving input ordering.
                    producer_event.synchronize()
            # Measure only this device's actual input-copy and forward
            # service time. Starting the timer BEFORE waiting on GPU 0
            # misattributes unrelated producer backlog to GPU 1/2, causing
            # measured capacity weights to send extra work to the already
            # busy producer and leave the second GPU idle. The event itself
            # is enqueued after the dependency, without any CPU sync.
            if sampled_pixels:
                try:
                    started = torch.cuda.Event(enable_timing=True)
                    started.record(stream)
                except (RuntimeError,NotImplementedError,AttributeError):
                    started = None
            result = model(*factory())
            if started is not None:
                try:
                    finished = torch.cuda.Event(enable_timing=True)
                    finished.record(stream)
                except (RuntimeError,NotImplementedError,AttributeError):
                    started = finished = None
            return result, (started,finished,sampled_pixels)

    def run_prepared(self, models, input_factories, devices=None, *,
                     pixel_work=None):
        """Prepare GPU inputs and launch their forwards concurrently.

        Factories are side-effect-free, single-use callables that return
        the positional inputs for one independent model. CPU records are
        immutable and GPU source tensors remain caller-owned until this
        call returns. Models and optimizers are NOT cloned or stepped.
        No GPU-wide barrier occurs: a producer event is recorded on the
        primary stream and waited on only by the secondary streams.
        """
        if self._closed or current_thread() is not self._owner:
            raise RuntimeError("GPU forward preparation requires its live owner")
        ordinals = self.devices if devices is None else tuple(devices)
        if (not 2 <= len(ordinals) <= len(self.devices) or
                ordinals != self.devices[:len(ordinals)] or
                len(models)!=len(ordinals) or
                len(input_factories)!=len(ordinals) or
                any(not callable(factory) for factory in input_factories) or
                (pixel_work is not None and
                 (len(pixel_work)!=len(ordinals) or
                  any(type(n) is not int or n < 0 for n in pixel_work)))):
            raise ValueError("GPU prepared cohort must match its leased devices")
        streams = [torch.cuda.current_stream(device) for device in ordinals]
        # Avoid allocating and destroying an inter-device ordering event
        # for every small pair of PPO map shapes. Calls never overlap on
        # this single-owner pool; workers enqueue all waits before return.
        producer = self._producer_event
        producer.record(streams[0])
        self._producer_event_records += 1
        grad_enabled = torch.is_grad_enabled()
        autocast_enabled = torch.is_autocast_enabled("cuda")
        autocast_dtype = torch.get_autocast_dtype("cuda")
        # Sample every fourth real two-card forward so even mixed-map
        # training exposes per-card service times. Only same-shape,
        # balanced samples may influence adaptive GPU work allocation.
        # Pixel-work is None for heterogeneous map pairs.
        comparable = (pixel_work is not None and
                      min(pixel_work)>0 and
                      max(pixel_work)/min(pixel_work)<=1.12 and
                      ordinals not in self._admitted_capacity_weights)
        sample = (self.prepared_calls % 4 == 0 and
                  len(self._pending_device_timings)<32)
        if len(ordinals)==2:
            # Launch GPU 1's worker first, then enqueue GPU 0's forward
            # directly on the owning learner thread. The owner is otherwise
            # idle waiting for two executor dispatches on every tiny map
            # pair. This eliminates one queue/context-switch round trip
            # without serializing the two GPU streams or changing autograd
            # ownership. The secondary worker still waits for the producer
            # event before touching any source-GPU tensors.
            peer = self._workers.submit(
                self._prepared_forward,models[1],input_factories[1],
                ordinals[1],streams[1],producer,
                grad_enabled,autocast_enabled,autocast_dtype,
                (pixel_work[1] if comparable else -1) if sample else 0)
            try:
                primary = self._prepared_forward(
                    models[0],input_factories[0],ordinals[0],streams[0],
                    None,grad_enabled,autocast_enabled,autocast_dtype,
                    (pixel_work[0] if comparable else -1) if sample else 0)
            except BaseException:
                # No optimizer step may proceed while a peer dispatch is
                # still using the current minibatch's source tensors.
                wait((peer,))
                raise
            collected = [primary,peer.result()]
            self.owner_inline_prepared_calls += 1
        else:
            futures = [self._workers.submit(
                self._prepared_forward, model, factory, device, stream,
                producer if i else None,
                grad_enabled, autocast_enabled, autocast_dtype,
                (pixel_work[i] if comparable else -1) if sample else 0)
                for i,(model,factory,device,stream) in enumerate(zip(
                    models,input_factories,ordinals,streams))]
            # Join Python dispatch, not GPU completion. Original tensors
            # and autograd operations retain their stream dependencies.
            wait(futures)
            collected = [future.result() for future in futures]
        self.calls += 1
        self.prepared_calls += 1
        if sample:
            for device,(_,timing) in zip(ordinals,collected):
                begin,end,pixels = timing
                if begin is not None and end is not None:
                    self._pending_device_timings.append(
                        (device,begin,end,pixels))
        self._collect_device_timings()
        return [output for output,_ in collected]

    def close(self):
        if current_thread() is not self._owner:
            raise RuntimeError("GPU forward pool must close on its owner")
        if self._closed:
            return
        self._closed = True
        self._workers.shutdown(wait=True)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
