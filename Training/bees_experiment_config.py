"""Freeze and verify experiment-specific ML-Agents configuration."""
from __future__ import annotations
import argparse
import hashlib
import json
import math
from pathlib import Path
import yaml

HYPER = {"beta", "lambd", "learning_rate", "batch_size", "buffer_size", "num_epoch"}
NETWORK = {"num_layers", "normalize", "hidden_units"}
BEHAVIOR = "BeesRL1v1"

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def resolved(value):
    b = value["behaviors"][BEHAVIOR]
    return {"hyperparameters": b["hyperparameters"],
            "network_settings": b["network_settings"],
            "seed": value.get("env_settings", {}).get("seed", 0),
            "trainer_type": b["trainer_type"]}

def prepare(base, overrides, output):
    value = yaml.safe_load(base.read_text(encoding="utf-8"))
    if not isinstance(overrides, dict) or set(overrides) - {"hyperparameters", "network_settings", "seed"}:
        raise ValueError("Only PPO settings, supported network settings and seed may be overridden.")
    b = value["behaviors"][BEHAVIOR]
    for section, allowed in (("hyperparameters", HYPER), ("network_settings", NETWORK)):
        changes = overrides.get(section, {})
        if not isinstance(changes, dict) or set(changes) - allowed:
            raise ValueError("Unsupported " + section + " override.")
        b[section].update(changes)
    hp, net = b["hyperparameters"], b["network_settings"]
    for key in ("beta", "lambd", "learning_rate"):
        n = hp[key]
        if type(n) not in (int, float) or not math.isfinite(n):
            raise ValueError(key + " must be finite.")
    if hp["beta"] < 0 or not 0 <= hp["lambd"] <= 1 or hp["learning_rate"] <= 0:
        raise ValueError("Require beta >= 0, lambda in [0,1], learning rate > 0.")
    for key in ("batch_size", "buffer_size", "num_epoch"):
        if type(hp[key]) is not int or hp[key] <= 0:
            raise ValueError(key + " must be a positive integer.")
    if hp["buffer_size"] < hp["batch_size"] or hp["buffer_size"] % hp["batch_size"]:
        raise ValueError("Buffer size must be a multiple of batch size.")
    if net.get("hidden_units") != 128 or net.get("memory") is not None:
        raise ValueError("v26 requires a feedforward policy with width 128.")
    if type(net["num_layers"]) is not int or not 1 <= net["num_layers"] <= 8:
        raise ValueError("Layer count must be an integer in 1..8.")
    if type(net["normalize"]) is not bool:
        raise ValueError("Normalization must be true or false.")
    seed = overrides.get("seed", value.get("env_settings", {}).get("seed", 0))
    if type(seed) is not int or not 0 <= seed < 2**31:
        raise ValueError("Seed must be an integer in 0..2^31-1.")
    value.setdefault("env_settings", {})["seed"] = seed
    # These experiments always start a new optimizer/policy lineage.
    if value.get("checkpoint_settings", {}).get("initialize_from"):
        raise ValueError("Fresh experiments cannot initialize from another run.")
    from mlagents.plugins.trainer_type import register_trainer_plugins
    from mlagents.trainers.settings import RunOptions
    register_trainer_plugins()
    RunOptions.from_dict(value)
    text = yaml.safe_dump(value, sort_keys=False)
    sha = hashlib.sha256(text.encode("utf-8")).hexdigest()
    target = output / (sha + ".yaml")
    output.mkdir(parents=True, exist_ok=True)
    if target.exists():
        if digest(target) != sha:
            raise ValueError("Frozen experiment configuration has changed.")
    else:
        with target.open("x", encoding="utf-8", newline="\n") as f:
            f.write(text)
    return {"path": str(target.resolve()), "sha256": sha,
            "base_sha256": digest(base), "initialization": "fresh",
            "resolved": resolved(value)}

def verify(expected, actual):
    record = resolved(yaml.safe_load(expected.read_text(encoding="utf-8")))
    value = yaml.safe_load(actual.read_text(encoding="utf-8"))
    if resolved(value) != record:
        raise ValueError("Generated trainer configuration differs from the queued experiment.")
    return {"path": str(actual.resolve()), "sha256": digest(actual),
            "resolved": resolved(value)}

def main():
    p = argparse.ArgumentParser()
    p.add_argument("command", choices=["prepare", "verify"])
    p.add_argument("--base", type=Path)
    p.add_argument("--overrides", default="{}")
    p.add_argument("--output", type=Path)
    p.add_argument("--expected", type=Path)
    p.add_argument("--actual", type=Path)
    a = p.parse_args()
    result = prepare(a.base, json.loads(a.overrides), a.output) if a.command == "prepare" else verify(a.expected, a.actual)
    print(json.dumps(result, sort_keys=True))
if __name__ == "__main__":
    main()
