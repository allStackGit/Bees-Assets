#!/usr/bin/env python3
"""Resident policy inference and behavior sampling for the native v26 game.

stdin and stdout are a VERSIONED BINARY PROTOCOL; stdout is not a log.
The manifest supplies TorchScript, ONNX or ML-Agents actor checkpoint weights.
The ML-Agents backend returns the source sampler's raw continuous samples
and selected action log probabilities alongside the environment actions.
Value estimates, trajectory ownership and the group critic belong to the learner.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import sys

import numpy as np

OBS_FLOATS = 7743
CONTINUOUS = 16
DISCRETE = 6
MASK_WIDTH = 15  # Five fire choices × 2, then five ship specials
ROW_META_BYTES = 16
MAX_SIGNATURE_BYTES = 8192
MAX_BATCH_ROWS = 65536
VERSION = 2
MAX_MANIFEST_PATH_BYTES = 4096
HEADER = struct.Struct("<4sIQI")
READY = struct.Struct("<4sIIH")
# Mirrors the Rust per-row wire header: slot(4), team(1), fire(5),
# special(5), reserved(1). One NumPy view replaces per-agent Python
# unpacking, slicing and array construction in large inference batches.
ROW_META_DTYPE = np.dtype([
    ("slot", "<u4"),
    ("team", "u1"),
    ("fire", "u1", (5,)),
    ("special", "u1", (5,)),
    ("reserved", "u1"),
])
assert ROW_META_DTYPE.itemsize == ROW_META_BYTES


def read_exact(reader, count: int) -> bytearray | None:
    """Return None only for clean EOF before a header; never a short frame."""
    output = bytearray(count)
    view = memoryview(output)
    offset = 0
    while offset < count:
        received = reader.readinto(view[offset:])
        if not received:
            if offset == 0:
                return None
            raise EOFError("incomplete inference wire frame")
        offset += received
    return output


class TorchScriptPolicy:
    def __init__(self, model_file: Path, config: dict):
        # PyTorch is imported only when a TorchScript model is configured;
        # ONNX-only inference does not require the full training runtime.
        import torch

        self.torch = torch
        device = config.get("device", "cpu")
        if not isinstance(device, str) or not device:
            raise ValueError("invalid TorchScript device")
        self.device = torch.device(device)
        self.model = torch.jit.load(str(model_file), map_location=self.device)
        self.model.eval()

    def predict(self, observations: np.ndarray, allowed: np.ndarray):
        torch = self.torch
        source = torch.from_numpy(np.ascontiguousarray(observations)).to(self.device)
        mask = torch.from_numpy(np.ascontiguousarray(allowed)).to(self.device)
        with torch.inference_mode():
            result = self.model(source, mask)
        if not isinstance(result, (list, tuple)) or len(result) != 2:
            raise ValueError("TorchScript policy must return two action tensors")
        cont, disc = result
        if not isinstance(cont, torch.Tensor) or not isinstance(disc, torch.Tensor):
            raise ValueError("TorchScript actions must be tensors")
        return cont.detach().to("cpu").numpy(), disc.detach().to("cpu").numpy()


class OnnxPolicy:
    def __init__(self, model_file: Path, config: dict):
        try:
            import onnxruntime as ort
        except ImportError as error:
            raise ValueError("ONNX policy requires onnxruntime") from error
        inputs = config.get("inputs")
        outputs = config.get("outputs")
        if not isinstance(inputs, dict) or not isinstance(outputs, dict):
            raise ValueError("ONNX policy requires explicit named inputs/outputs")
        obs_input = inputs.get("observations")
        mask_input = inputs.get("allowed_mask")
        cont_output = outputs.get("continuous")
        disc_output = outputs.get("discrete")
        combined_output = outputs.get("combined")
        if (cont_output is None) != (disc_output is None):
            raise ValueError("ONNX requires both separate outputs or one combined output")
        if (cont_output is None) == (combined_output is None):
            raise ValueError("ONNX output layout must be unambiguous")
        names = [obs_input, mask_input, cont_output or combined_output]
        if disc_output is not None:
            names.append(disc_output)
        if any(not isinstance(name, str) or not name for name in names):
            raise ValueError("ONNX names cannot be empty")
        self.observation_input = obs_input
        self.mask_input = mask_input
        self.continuous_output = cont_output
        self.discrete_output = disc_output
        self.combined_output = combined_output
        providers = config.get("providers", ["CPUExecutionProvider"])
        if not isinstance(providers, list) or not providers or any(
            not isinstance(provider, str) for provider in providers
        ):
            raise ValueError("ONNX providers must be an ordered list")
        options = None
        intra_op_threads = config.get("intra_op_threads")
        if intra_op_threads is not None:
            if type(intra_op_threads) is not int or not 1 <= intra_op_threads <= 2147483647:
                raise ValueError("ONNX intra_op_threads must be a positive int32")
            options = ort.SessionOptions()
            options.intra_op_num_threads = intra_op_threads
        self.session = ort.InferenceSession(
            str(model_file), sess_options=options, providers=providers
        )
        # Fail rather than silently claiming an unavailable accelerator.
        if providers[0] not in self.session.get_providers():
            raise ValueError("requested primary ONNX provider is not available")
        actual_inputs = {item.name: item for item in self.session.get_inputs()}
        actual_outputs = {item.name for item in self.session.get_outputs()}
        if obs_input == mask_input or not {obs_input, mask_input}.issubset(actual_inputs):
            raise ValueError("ONNX missing configured observation/mask inputs")
        if any(name not in actual_outputs for name in names[2:]):
            raise ValueError("ONNX missing configured action outputs")
        # Extra required model inputs must not be invented by the worker.
        if len(actual_inputs) != 2:
            raise ValueError("ONNX model requires additional unsupported inputs")
        for name, width in ((obs_input, OBS_FLOATS), (mask_input, MASK_WIDTH)):
            info = actual_inputs[name]
            if (info.type != "tensor(float)" or len(info.shape) != 2
                    or isinstance(info.shape[1], int) and info.shape[1] != width):
                raise ValueError(f"ONNX input {name!r} must accept float32 [batch, {width}]")

    def predict(self, observations: np.ndarray, allowed: np.ndarray):
        feeds = {
            self.observation_input: np.ascontiguousarray(observations),
            self.mask_input: np.ascontiguousarray(allowed),
        }
        if self.combined_output is not None:
            outputs = self.session.run([self.combined_output], feeds)
            combined = np.asarray(outputs[0])
            if combined.ndim != 2 or combined.shape[1] != CONTINUOUS + DISCRETE:
                raise ValueError("ONNX combined actions must contain exactly 22 columns")
            cont = combined[:, :CONTINUOUS]
            disc = combined[:, CONTINUOUS:]
            if not np.all(np.isfinite(disc)) or not np.all(disc == np.floor(disc)):
                raise ValueError("ONNX combined discrete actions must be integral")
            return cont, disc.astype(np.int64)
        return tuple(self.session.run(
            [self.continuous_output, self.discrete_output], feeds
        ))


def install_bees_actor():
    training_path = str(Path(__file__).resolve().parents[2] / "Training")
    if training_path not in sys.path:
        sys.path.insert(0, training_path)
    from bees_mlagents_ppo_compat import (
        install_continuous_sigma_guard, install_inactive_continuous_action_masking,
    )
    from bees_mlagents_structured_policy import install_structured_policy

    install_structured_policy()
    install_inactive_continuous_action_masking()
    install_continuous_sigma_guard()


def mlagents_behavior_spec():
    from mlagents_envs.base_env import (
        ActionSpec, BehaviorSpec, DimensionProperty, ObservationSpec, ObservationType,
    )

    return BehaviorSpec([ObservationSpec(
        (OBS_FLOATS,), (DimensionProperty.NONE,), ObservationType.DEFAULT, "VectorSensor"
    )], ActionSpec(CONTINUOUS, (2, 2, 2, 2, 2, 5)))


class MlagentsPolicy:
    """Use the same v26 actor and action sampler as the MA-POCA trainer."""

    def __init__(self, model_file: Path, config: dict):
        import torch
        from mlagents.trainers.settings import NetworkSettings
        from mlagents.trainers.torch_entities.networks import SimpleActor

        install_bees_actor()
        spec = mlagents_behavior_spec()
        self.torch = torch
        self.device = torch.device(config.get("device", "cpu"))
        self.actor = SimpleActor(
            observation_specs=spec.observation_specs,
            network_settings=NetworkSettings(normalize=True, hidden_units=128, num_layers=3),
            action_spec=spec.action_spec,
        ).to(self.device)
        checkpoint = torch.load(model_file, map_location="cpu", weights_only=True)
        self.actor.load_state_dict(checkpoint["Policy"], strict=True)
        self.actor.eval()

    def predict(self, observations, allowed):
        torch = self.torch
        observations = torch.from_numpy(np.ascontiguousarray(observations)).to(self.device)
        allowed = torch.from_numpy(np.ascontiguousarray(allowed)).to(self.device)
        with torch.inference_mode():
            action, stats, _ = self.actor.get_action_and_stats([observations], masks=allowed)
            applied = stats["env_action"]
            sampled = action.to_action_tuple()
            log_probs = stats["log_probs"].flatten().cpu().numpy()
        return applied.continuous, applied.discrete, np.concatenate(
            (sampled.continuous, log_probs), axis=1
        )


def read_reused(reader, storage: bytearray, count: int):
    """Reuse the largest observation receive buffer across model batches."""
    if len(storage) < count:
        storage = bytearray(count)
    view = memoryview(storage)[:count]
    offset = 0
    while offset < count:
        received = reader.readinto(view[offset:])
        if not received:
            raise EOFError("incomplete inference observation payload")
        offset += received
    return storage, view


def stream_sha256(artifact) -> str:
    digest = hashlib.sha256()
    for chunk in iter(lambda: artifact.read(1024 * 1024), b""):
        digest.update(chunk)
    return digest.hexdigest()


def file_sha256(path: Path) -> str:
    with path.open("rb") as artifact:
        return stream_sha256(artifact)


def checked_manifest(path: Path, expected_digest: bytes | None = None,
                     resident_models: dict | None = None):
    manifest_bytes = path.read_bytes()
    manifest_digest = hashlib.sha256(manifest_bytes).digest()
    if expected_digest is not None and manifest_digest != expected_digest:
        raise ValueError("model-update manifest changed before loading")
    manifest = json.loads(manifest_bytes.decode("utf-8"))
    if not isinstance(manifest, dict) or manifest.get("format") != "bees-native-model-v1":
        raise ValueError("manifest must declare bees-native-model-v1")
    if type(manifest.get("abi_version")) is not int or manifest["abi_version"] != 26:
        raise ValueError("native model must use Bees policy ABI v26")
    signature = manifest.get("policy_signature")
    if not isinstance(signature, str):
        raise ValueError("full frozen v26 policy_signature is mandatory")
    signature_bytes = signature.encode("utf-8")
    if not 0 < len(signature_bytes) <= MAX_SIGNATURE_BYTES:
        raise ValueError("invalid model signature length")
    configurations = manifest.get("models")
    if not isinstance(configurations, list) or not configurations:
        raise ValueError("manifest must contain loaded model slots")
    policies = {}
    # Several independently routed teams can share one immutable checkpoint.
    # Cache by all backend settings and verified bytes, not the slot number
    # or artifact path, so distinct devices/providers never share a model.
    loaded_models = {}
    for config in configurations:
        if not isinstance(config, dict):
            raise ValueError("invalid model slot configuration")
        slot = config.get("slot")
        model_path = config.get("path")
        backend = config.get("backend")
        if type(slot) is not int or not 0 <= slot < (1 << 32):
            raise ValueError("model slot must be a uint32")
        if slot in policies:
            raise ValueError(f"duplicate model slot: {slot}")
        if not isinstance(model_path, str) or not model_path:
            raise ValueError("model path is required")
        # Rust passes an absolute verbatim path on Windows. Normalize the
        # slot path before opening so relative parent components work there.
        model_file = path.parent / model_path
        if os.name == "nt":
            drive, tail = os.path.splitdrive(model_file)
            if drive.startswith("\\\\?\\"):
                model_file = Path(drive + os.path.normpath(tail))
        expected_sha256 = config.get("sha256")
        if not isinstance(expected_sha256, str) or (
            len(expected_sha256) != 64
        ) or any(char not in "0123456789abcdef" for char in expected_sha256):
            raise ValueError("model requires a canonical lowercase SHA-256 digest")
        if file_sha256(model_file) != expected_sha256:
            raise ValueError(f"model artifact content changed: slot {slot}")
        if backend not in ("torchscript", "onnx", "mlagents"):
            raise ValueError(f"unsupported native model backend: {backend}")
        settings = {
            key: value for key, value in config.items()
            if key not in ("slot", "path", "sha256")
        }
        identity = (expected_sha256, json.dumps(settings, sort_keys=True))
        model = loaded_models.get(identity)
        if model is None and resident_models is not None:
            # An episode-boundary model update can change only the learner
            # while retaining several verified frozen opponents. Their
            # actor/ONNX sessions are still loaded, so neither allocate a
            # second GPU copy nor reset unchanged state on a reload.
            model = resident_models.get(identity)
        if model is None:
            if backend == "torchscript":
                model = TorchScriptPolicy(model_file, config)
            elif backend == "onnx":
                model = OnnxPolicy(model_file, config)
            else:
                model = MlagentsPolicy(model_file, config)
        loaded_models[identity] = model
        # The caller reconstructs its live cache only AFTER every candidate
        # slot, signature and manifest update has been accepted. A rejected
        # candidate can never displace the active cache.
        model._verified_identity = identity
        # Libraries reopen model paths during deserialization. Detect an
        # artifact replaced between the first verified read and actual
        # model load, rather than attributing different weights to the
        # manifest's immutable expected digest.
        if file_sha256(model_file) != expected_sha256:
            raise ValueError(f"model artifact changed during load: slot {slot}")
        policies[slot] = model
    # Mixed manifests are supported: ML-Agents slots supply sampled behavior
    # statistics, while ONNX/TorchScript opponent slots supply only actions.
    # collect_predictions emits a behavior frame only when this particular
    # inference batch contains a sampled policy, zero-filling nonlearner rows.
    return signature_bytes, policies, manifest_digest


def decode_row_metadata(meta: bytearray, rows: int):
    fields = np.frombuffer(meta, dtype=ROW_META_DTYPE, count=rows)
    fire_bytes = fields["fire"]
    special_bytes = fields["special"]
    # NumPy reduction dispatch outweighs Python indexing for 1v1 and
    # other tiny batches. Keep the tiny path fast without trading away
    # vectorized validation for hundreds or thousands of agents.
    if rows <= 3:
        if any(meta[index * ROW_META_BYTES + 15] for index in range(rows)):
            raise ValueError("nonzero reserved metadata")
        if any(value > 1 for index in range(rows) for value in
               meta[index * ROW_META_BYTES + 5:index * ROW_META_BYTES + 15]):
            raise ValueError("invalid boolean action mask")
        if any(meta[index * ROW_META_BYTES + 10] == 0 for index in range(rows)):
            raise ValueError("NoSpecial must remain enabled")
    else:
        if np.any(fields["reserved"]):
            raise ValueError("nonzero reserved metadata")
        if np.any(fire_bytes > 1) or np.any(special_bytes > 1):
            raise ValueError("invalid boolean action mask")
        if np.any(special_bytes[:, 0] == 0):
            raise ValueError("NoSpecial must remain enabled")

    # Contiguous, batch-wide copies are cheaper than allocating two arrays
    # per agent; the slot IDs remain a borrowed view of this input frame.
    slots = fields["slot"]
    fire = fire_bytes.astype(np.bool_)
    special = special_bytes.astype(np.bool_)
    allow = np.ones((rows, MASK_WIDTH), dtype=np.float32)
    allow[:, 1:10:2] = fire
    allow[:, 10:15] = special
    return slots, fire, special, allow

def collect_predictions(
    policy_map, slots, observations, fire, special, allowed, measurements=None,
):
    rows = len(slots)
    continuous = np.empty((rows, CONTINUOUS), dtype="<f4")
    discrete = np.empty((rows, DISCRETE), dtype=np.uint8)
    behavior = None

    # One inference pass per distinct resident model, not per routing slot.
    # An initial two-team PPO bootstrap deliberately has separate slot IDs
    # pointing at identical weights: this makes both teams one GPU/CPU batch.
    # Distinct checkpoints and different device/provider settings keep their
    # independent execution and actor sampling streams.
    single_slot = bool(np.all(slots == slots[0]))
    unique_slots = (slots[:1] if single_slot else np.unique(slots))
    groups = {}
    for slot in unique_slots:
        slot_id = int(slot)
        if slot_id not in policy_map:
            raise ValueError(f"no genuine model loaded for slot {slot_id}")
        model = policy_map[slot_id]
        identity = id(model)
        if identity not in groups:
            groups[identity] = (model, [])
        groups[identity][1].append(slot_id)
    fully_sampled = all(isinstance(model, MlagentsPolicy)
                        for model, _ in groups.values())
    if measurements is not None:
        measurements["batches"] += 1
        measurements["agent_rows"] += rows
        measurements["slot_forwards_without_sharing"] += len(unique_slots)
        measurements["actual_model_forwards"] += len(groups)

    for model, model_slots in groups.values():
        if len(groups) == 1:
            # All rows use the same loaded checkpoint. Keep their actual
            # source order and borrow the entire contiguous observation
            # matrix rather than constructing per-slot gathered copies.
            indices = slice(None)
            count = rows
            sub_obs, sub_mask = observations, allowed
        else:
            matching = (slots == model_slots[0] if len(model_slots) == 1
                        else np.isin(slots, model_slots))
            indices = np.flatnonzero(matching)
            count = len(indices)
            # Contiguous model rows borrow their existing memory even when
            # they arrive through several distinct routing slots.
            first, last = int(indices[0]), int(indices[-1])
            if last - first + 1 == count:
                indices = slice(first, last + 1)
            sub_obs, sub_mask = observations[indices], allowed[indices]
        prediction = model.predict(sub_obs, sub_mask)
        if not isinstance(prediction, (tuple, list)) or len(prediction) not in (2, 3):
            raise ValueError("policy backend must return two actions and optional behavior statistics")
        sampled = isinstance(model, MlagentsPolicy)
        if (len(prediction) == 3) != sampled:
            raise ValueError("ML-Agents slots must return behavior statistics; inference-only slots must not")
        out_cont, out_disc = prediction[:2]
        if sampled:
            stats = prediction[2]
            if stats.shape != (count, CONTINUOUS * 2 + DISCRETE) or not np.isfinite(stats).all():
                raise ValueError("invalid sampled action or behavior log probabilities")
            if behavior is None:
                # The protocol emits one all-row frame. Non-sampled
                # TorchScript/ONNX opponents must retain zeroed statistics.
                shape = (rows, CONTINUOUS * 2 + DISCRETE)
                behavior = (
                    np.empty(shape, dtype="<f4") if fully_sampled
                    else np.zeros(shape, dtype="<f4")
                )
            behavior[indices] = stats
        expected_cont = (count, CONTINUOUS)
        expected_disc = (count, DISCRETE)
        if out_cont.shape != expected_cont or out_disc.shape != expected_disc:
            raise ValueError("incompatible native policy output shape")
        if not np.all(np.isfinite(out_cont)):
            raise ValueError("nonfinite continuous actions")
        if out_disc.dtype.kind not in ("i", "u"):
            raise ValueError("discrete branches must be integer tensors")
        if np.any(out_disc < 0) or np.any(out_disc > 255):
            raise ValueError("discrete output cannot be represented as u8")
        continuous[indices] = np.asarray(out_cont, dtype="<f4")
        discrete[indices] = np.asarray(out_disc, dtype=np.uint8)
    if np.any(discrete[:, :5] > 1) or np.any(discrete[:, 5] > 4):
        raise ValueError("out-of-range discrete branch")
    for slot in range(5):
        if np.any((discrete[:, slot] == 1) & ~fire[:, slot]):
            raise ValueError("model selected masked weapon firing")
    if np.any(~special[np.arange(rows), discrete[:, 5]]):
        raise ValueError("model selected unavailable special action")
    return continuous, discrete, behavior

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()
    signature, policy_map, manifest_digest = checked_manifest(args.manifest)
    resident_models = {
        model._verified_identity: model for model in policy_map.values()
    }
    measurements = {
        "batches": 0, "agent_rows": 0,
        "slot_forwards_without_sharing": 0, "actual_model_forwards": 0,
        "model_updates": 0,
    }

    def report_model_residency():
        # JSON on stderr is deliberately outside the binary model protocol.
        # At fixed low frequency it reports actual model calls, avoided
        # passes and retained weights without timing the Python hot loop.
        print(json.dumps({
            "kind": "native_policy_inference_efficiency",
            **measurements,
            "model_forwards_saved":
                measurements["slot_forwards_without_sharing"] -
                measurements["actual_model_forwards"],
            "model_slots": len(policy_map),
            "distinct_loaded_models": len(resident_models),
        }, sort_keys=True), file=sys.stderr, flush=True)

    report_model_residency()
    # A holdout benchmark can pin the action-sampling RNG without changing
    # ordinary gameplay/training or the versioned inference wire protocol.
    # Seed after loading model weights: constructor initialization may
    # consume random numbers even though its parameters are overwritten.
    policy_seed = os.environ.get("BEES_NATIVE_EVALUATION_SEED")
    if policy_seed is not None:
        if (not policy_seed.isascii() or not policy_seed.isdecimal() or
                len(policy_seed) > 19):
            raise ValueError("evaluation sampling seed must be an unsigned int63")
        value = int(policy_seed)
        if value >= (1 << 63):
            raise ValueError("evaluation sampling seed exceeds int63")
        import random
        random.seed(value)
        np.random.seed(value % (1 << 32))
        if any(isinstance(model, (TorchScriptPolicy, MlagentsPolicy))
               for model in policy_map.values()):
            import torch
            torch.manual_seed(value)
    reader = sys.stdin.buffer
    writer = sys.stdout.buffer
    writer.write(READY.pack(b"BHOK", VERSION, 26, len(signature)))
    writer.write(signature)
    writer.write(manifest_digest)
    writer.flush()
    observation_storage = bytearray()
    while True:
        data = read_exact(reader, HEADER.size)
        if data is None:
            break
        magic, version, request_id, rows = HEADER.unpack(data)
        if version != VERSION:
            raise ValueError("invalid policy worker protocol version")
        if magic == b"BMOD":
            # A model-update control frame is processed strictly between
            # inference batches. New models are loaded and SHA-verified
            # before replacing the active slot map; a rejected update
            # leaves every previously loaded model available.
            if not 32 < rows <= MAX_MANIFEST_PATH_BYTES + 32:
                raise ValueError("invalid model-update manifest payload size")
            payload = read_exact(reader, rows)
            if payload is None:
                raise EOFError("incomplete model-update manifest payload")
            expected_digest = bytes(payload[:32])
            new_map = None
            try:
                manifest_path = Path(bytes(payload[32:]).decode("utf-8"))
                if not manifest_path.is_absolute():
                    raise ValueError("model-update manifest must be absolute")
                new_signature, new_map, new_digest = checked_manifest(
                    manifest_path, expected_digest, resident_models
                )
                if new_signature != signature:
                    raise ValueError("model-update policy ABI signature mismatch")
                if new_map.keys() != policy_map.keys():
                    raise ValueError("model-update changes active policy slots")
            except Exception as error:
                # Backend libraries raise different Exception subclasses.
                # A failed candidate must not replace or poison the active
                # policy, nor leave a successfully loaded candidate resident.
                new_map = None
                print(f"Native policy reload rejected: {error}",
                      file=sys.stderr, flush=True)
                writer.write(HEADER.pack(b"MNOK", VERSION, request_id, 0))
                writer.flush()
                continue
            old_map = policy_map
            policy_map = new_map
            # Replace, never accumulate: obsolete opponents and old learner
            # weights must be released after a successful model transition.
            resident_models = {
                model._verified_identity: model for model in policy_map.values()
            }
            manifest_digest = new_digest
            measurements["model_updates"] += 1
            new_map = None
            del old_map
            report_model_residency()
            writer.write(HEADER.pack(b"MACK", VERSION, request_id, len(policy_map)))
            writer.write(manifest_digest)
            writer.flush()
            continue
        if magic != b"BINF":
            raise ValueError("invalid inference request header")
        if not 0 < rows <= MAX_BATCH_ROWS:
            raise ValueError("invalid model batch size")
        meta = read_exact(reader, rows * ROW_META_BYTES)
        if meta is None:
            raise EOFError("incomplete inference metadata")
        observation_storage, payload = read_reused(
            reader, observation_storage, rows * OBS_FLOATS * 4
        )
        slots, fire, special, allowed = decode_row_metadata(meta, rows)
        observation = np.frombuffer(payload, dtype="<f4").reshape(
            (rows, OBS_FLOATS)
        )
        if not np.all(np.isfinite(observation)):
            raise ValueError("nonfinite policy observation")
        continuous, discrete, behavior = collect_predictions(
            policy_map, slots, observation, fire, special, allowed, measurements
        )
        if measurements["batches"] % 2048 == 0:
            report_model_residency()
        writer.write(HEADER.pack(b"BTRN" if behavior is not None else b"BOUT", VERSION, request_id, rows))
        writer.write(continuous.tobytes(order="C"))
        writer.write(discrete.tobytes(order="C"))
        if behavior is not None:
            writer.write(behavior.tobytes(order="C"))
        writer.flush()
        # Release NumPy/memoryview references before expanding the next
        # batch's storage; smaller/equal batches reuse the same allocation.
        del observation, payload
    report_model_residency()


if __name__ == "__main__":
    try:
        main()
    except (OSError, EOFError, ValueError, RuntimeError) as error:
        print(f"Native policy worker failed: {error}", file=sys.stderr, flush=True)
        sys.exit(1)
