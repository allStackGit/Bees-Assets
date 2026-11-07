#!/usr/bin/env python3
"""Resident, real PyTorch policy inference for the native v26 game and worker.

stdin and stdout are a VERSIONED BINARY PROTOCOL; stdout is not a log.
The manifest supplies real TorchScript or source-exported ONNX policy weights.
Each model must implement forward(obs[N,7743], allow_mask[N,15]) ->
(continuous[N,16], discrete[N,6]) with learned action sampling/masking.
No placeholder policies, dummy actions or copied combat rules are supplied.

This is an inference/evaluation bridge only. A PPO learner still needs the
real action log probabilities, value estimates, trajectory ownership and
group critic. Running this worker does NOT make native training complete.
"""

from __future__ import annotations

import argparse
import json
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
VERSION = 1
HEADER = struct.Struct("<4sIQI")
READY = struct.Struct("<4sIIH")
U32 = struct.Struct("<I")


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
        self.session = ort.InferenceSession(str(model_file), providers=providers)
        # Fail rather than silently claiming an unavailable accelerator.
        if providers[0] not in self.session.get_providers():
            raise ValueError("requested primary ONNX provider is not available")
        actual_inputs = {item.name for item in self.session.get_inputs()}
        actual_outputs = {item.name for item in self.session.get_outputs()}
        if not {obs_input, mask_input}.issubset(actual_inputs):
            raise ValueError("ONNX missing configured observation/mask inputs")
        if any(name not in actual_outputs for name in names[2:]):
            raise ValueError("ONNX missing configured action outputs")
        # Extra required model inputs must not be invented by the worker.
        if len(actual_inputs) != 2:
            raise ValueError("ONNX model requires additional unsupported inputs")

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


def checked_manifest(path: Path):
    manifest = json.loads(path.read_text(encoding="utf-8"))
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
        model_file = path.parent / model_path
        if backend == "torchscript":
            policies[slot] = TorchScriptPolicy(model_file, config)
        elif backend == "onnx":
            policies[slot] = OnnxPolicy(model_file, config)
        else:
            raise ValueError(f"unsupported native model backend: {backend}")
    return signature_bytes, policies


def decode_row_metadata(meta: bytearray, rows: int):
    slots = np.empty(rows, dtype=np.uint32)
    fire = np.empty((rows, 5), dtype=np.bool_)
    special = np.empty((rows, 5), dtype=np.bool_)
    for index in range(rows):
        start = index * ROW_META_BYTES
        slots[index] = U32.unpack_from(meta, start)[0]
        # The 1-byte faction/team ID is for routing diagnostics only; the
        # signed faction discriminator already resides in observation tail.
        if meta[start + 15] != 0:
            raise ValueError("nonzero reserved metadata")
        fire_bytes = meta[start + 5 : start + 10]
        special_bytes = meta[start + 10 : start + 15]
        if any(value not in (0, 1) for value in fire_bytes + special_bytes):
            raise ValueError("invalid boolean action mask")
        fire[index] = np.frombuffer(fire_bytes, dtype=np.uint8).astype(np.bool_)
        special[index] = np.frombuffer(special_bytes, dtype=np.uint8).astype(np.bool_)
        if not special[index, 0]:
            raise ValueError("NoSpecial must remain enabled")
    allow = np.ones((rows, MASK_WIDTH), dtype=np.float32)
    for slot in range(5):
        allow[:, slot * 2 + 1] = fire[:, slot]
    allow[:, 10:15] = special
    return slots, fire, special, allow


def collect_predictions(
    policy_map, slots, observations, fire, special, allowed,
):
    rows = len(slots)
    continuous = np.empty((rows, CONTINUOUS), dtype="<f4")
    discrete = np.empty((rows, DISCRETE), dtype=np.uint8)
    # Group by model slot without touching game state or changing row
    # identity. The common single-model case does not gather-copy obs.
    unique_slots = np.unique(slots)
    for slot in unique_slots:
        slot_id = int(slot)
        if slot_id not in policy_map:
            raise ValueError(f"no genuine model loaded for slot {slot_id}")
        model = policy_map[slot_id]
        indices = np.flatnonzero(slots == slot)
        if len(indices) == rows:
            sub_obs, sub_mask = observations, allowed
        else:
            sub_obs, sub_mask = observations[indices], allowed[indices]
        out_cont, out_disc = model.predict(sub_obs, sub_mask)
        expected_cont = (len(indices), CONTINUOUS)
        expected_disc = (len(indices), DISCRETE)
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
    return continuous, discrete


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()
    signature, policy_map = checked_manifest(args.manifest)
    reader = sys.stdin.buffer
    writer = sys.stdout.buffer
    writer.write(READY.pack(b"BHOK", VERSION, 26, len(signature)))
    writer.write(signature)
    writer.flush()
    observation_storage = bytearray()
    while True:
        data = read_exact(reader, HEADER.size)
        if data is None:
            break
        magic, version, request_id, rows = HEADER.unpack(data)
        if magic != b"BINF" or version != VERSION:
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
        continuous, discrete = collect_predictions(
            policy_map, slots, observation, fire, special, allowed
        )
        writer.write(HEADER.pack(b"BOUT", VERSION, request_id, rows))
        writer.write(continuous.tobytes(order="C"))
        writer.write(discrete.tobytes(order="C"))
        writer.flush()
        # Release NumPy/memoryview references before expanding the next
        # batch's storage; smaller/equal batches reuse the same allocation.
        del observation, payload


if __name__ == "__main__":
    try:
        main()
    except (OSError, EOFError, ValueError, RuntimeError) as error:
        print(f"Native policy worker failed: {error}", file=sys.stderr, flush=True)
        sys.exit(1)
