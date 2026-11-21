"""Install an approved native champion package and expose it in the game gallery.

Deployment content may arrive through a mounted share or a verified file
transfer. Installation copies only that package, then atomically advances
the local pointer. Playing a deployed policy uses the existing native
RunConfig, challenge archive and resident inference worker.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile

from native_game_registration import register_playable
from native_session_selection import _playable_run
from native_training_artifacts import atomic_json, file_sha256, sync_directory
from package_policy_manifest import verify_package


def document(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected JSON object: {path}")
    return value


def verified_deployment_policy(package):
    verified = verify_package(package)
    manifest_path = package / "manifest.json"
    model = document(manifest_path)["models"]
    if (len(model) != 1 or model[0]["slot"] != 0 or
            model[0]["backend"] not in ("mlagents", "onnx")):
        raise ValueError("native champion deployment must contain exactly one policy")
    return {**verified, "manifest": str(manifest_path),
            "learner_checkpoint": str(package / model[0]["path"])}


def verify_deployment(package, pointer):
    deployment_id = pointer.get("deployment_id")
    if (pointer.get("schema_version") != 1 or not isinstance(deployment_id, str) or
            len(deployment_id) != 31 or not deployment_id.startswith("native-") or
            any(c not in "0123456789abcdef" for c in deployment_id[7:])):
        raise ValueError("invalid native deployment pointer")
    metadata_path = package / "deployment.json"
    if (package.is_symlink() or metadata_path.is_symlink() or
            file_sha256(metadata_path) != pointer.get("deployment_sha256")):
        raise ValueError("native deployment pointer does not match its package")
    record = document(metadata_path)
    identity = record["identity"]
    digest = hashlib.sha256(json.dumps(
        identity, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
    ).encode("utf-8")).hexdigest()
    verified = verified_deployment_policy(package / "policy")
    model = document(Path(verified["manifest"]))
    checkpoint = Path(verified["learner_checkpoint"])
    if (record["identity_sha256"] != digest or
            deployment_id != "native-" + digest[:24] or
            record["deployment_id"] != deployment_id or
            identity.get("schema_version") != 1 or
            identity["kind"] != "native_continual_deployment" or
            identity["model_id"] != pointer.get("model_id") or
            identity["policy_signature"] != model["policy_signature"] or
            identity["compatibility"]["policy_abi_version"] != model["abi_version"] or
            identity["model_size_bytes"] != checkpoint.stat().st_size or
            identity["promotion_evidence"]["type"] not in (
                "passing-evaluation", "generation-zero-bootstrap") or
            record["policy_manifest_sha256"] != verified["manifest_sha256"] or
            file_sha256(checkpoint) != identity["checkpoint_sha256"]):
        raise ValueError("native deployment package identity differs from its pointer")
    return record, verified


def resolve_deployment(root):
    root = Path(root).resolve(strict=True)
    pointer = document(root / "current-native-deployment.json")
    deployment_id = pointer.get("deployment_id")
    if (not isinstance(deployment_id, str) or
            not deployment_id.startswith("native-") or len(deployment_id) != 31 or
            any(c not in "0123456789abcdef" for c in deployment_id[7:])):
        raise ValueError("invalid native deployment package name")
    return verify_deployment(root / "packages" / deployment_id, pointer)


def install_deployment(source, target):
    """Copy immutable package bytes before changing the recipient's pointer."""
    source = source.resolve(strict=True)
    target = target.resolve()
    pointer = document(source / "current-native-deployment.json")
    record, verified = resolve_deployment(source)
    if (record["deployment_id"] != pointer["deployment_id"] or
            file_sha256(source / "packages" / pointer["deployment_id"] / "deployment.json")
            != pointer["deployment_sha256"]):
        raise ValueError("source deployment changed during installation")
    packages = target / "packages"
    packages.mkdir(parents=True, exist_ok=True)
    package = packages / pointer["deployment_id"]
    if not package.exists():
        staging = Path(tempfile.mkdtemp(prefix=".install-native-", dir=packages))
        try:
            model_root = Path(verified["manifest"]).parent
            policy = staging / "policy"
            policy.mkdir()
            (policy / "models").mkdir()
            files = [
                (model_root / "receipt.json", policy / "receipt.json"),
                (model_root / "manifest.json", policy / "manifest.json"),
                (model_root.parent / "deployment.json", staging / "deployment.json"),
            ]
            files.extend((file, policy / "models" / file.name)
                         for file in (model_root / "models").iterdir())
            for src, dst in files:
                if src.is_symlink() or not src.is_file():
                    raise ValueError("native deployment contains a non-regular artifact")
                with src.open("rb") as stream, dst.open("xb") as sink:
                    shutil.copyfileobj(stream, sink, 1024 * 1024)
                    sink.flush()
                    os.fsync(sink.fileno())
            verify_deployment(staging, pointer)
            sync_directory(policy / "models")
            sync_directory(policy)
            sync_directory(staging)
            try:
                os.replace(staging, package)
            except OSError:
                if not package.is_dir():
                    raise
            sync_directory(packages)
        finally:
            if staging.exists():
                shutil.rmtree(staging)
    verify_deployment(package, pointer)
    atomic_json(target / "current-native-deployment.json", pointer)
    return pointer


def bind_game(deployment_root, run_path, output, save_root=None, game_id=None):
    """Bind every authored policy route to one approved current champion.

    This can drive a model-only match, or the gallery's existing human vs
    policy challenge. Both use the same original physical Levels. The
    immutable per-deployment run survives later pointer changes.
    """
    record, policy = resolve_deployment(deployment_root)
    identity = record["identity"]
    run_path = run_path.resolve(strict=True)
    source_digest = file_sha256(run_path)
    output = output.resolve()
    version = output / (record["deployment_id"] + "-" + source_digest[:16])
    version.mkdir(parents=True, exist_ok=True)
    manifest_path = version / "manifest.json"
    checkpoint = Path(policy["learner_checkpoint"])
    native_manifest = document(Path(policy["manifest"]))
    selected = native_manifest["models"][0]
    if len(native_manifest["models"]) != 1 or selected["slot"] != 0:
        raise ValueError("champion deployment must contain one policy")
    playable = _playable_run(run_path, {
        "checkpoint_sha256": identity["checkpoint_sha256"],
        "manifest": str(manifest_path),
    })
    routes = set()
    for level in playable["levels"]:
        slots = level.get("model_slots")
        if not isinstance(slots, dict) or not slots:
            raise ValueError("champion gameplay Level lacks authored model routes")
        routes.update(slots.values())
    if any(type(slot) is not int or not 0 <= slot < 1 << 32 for slot in routes):
        raise ValueError("champion gameplay has invalid model route")
    native_manifest["models"] = [
        {**selected, "slot": slot, "path": str(checkpoint)} for slot in sorted(routes)
    ]
    playable["host_recovery_checkpoint"] = None
    config_path = version / "run.json"
    for path, value in ((manifest_path, native_manifest), (config_path, playable)):
        if path.exists():
            if document(path) != value:
                raise ValueError("previous champion gameplay publication changed")
        else:
            atomic_json(path, value)
    if file_sha256(run_path) != source_digest:
        raise ValueError("champion gameplay source changed during publication")
    receipt = {
        "schema_version": 1, "kind": "native_continual_champion_playable",
        "selection": "approved_registry_champion",
        "deployment_id": record["deployment_id"],
        "model_id": identity["model_id"], "training_step": identity["training_step"],
        "learner_slot": min(routes), "checkpoint": str(checkpoint),
        "checkpoint_sha256": identity["checkpoint_sha256"],
        "manifest": str(manifest_path), "manifest_sha256": file_sha256(manifest_path),
        "source_run_config": str(run_path), "source_run_config_sha256": source_digest,
        "playable_run": str(config_path), "playable_run_sha256": file_sha256(config_path),
    }
    # One stable gallery registration follows the current deployment.
    # A running/archived battle retains the earlier immutable run/manifest.
    receipt_path = output / "current-champion-playable.json"
    atomic_json(receipt_path, receipt)
    if save_root is not None:
        register_playable(save_root, game_id, "Continual Bees champion", receipt_path)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    install = commands.add_parser("install")
    install.add_argument("source", type=Path)
    install.add_argument("target", type=Path)
    bind = commands.add_parser("bind")
    bind.add_argument("deployment_root", type=Path)
    bind.add_argument("run_config", type=Path)
    bind.add_argument("output", type=Path)
    bind.add_argument("--library-save-root", type=Path)
    bind.add_argument("--library-game-id", default="continual-champion")
    args = parser.parse_args()
    if args.command == "install":
        result = install_deployment(args.source, args.target)
    else:
        result = bind_game(args.deployment_root, args.run_config, args.output,
                           args.library_save_root, args.library_game_id)
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
