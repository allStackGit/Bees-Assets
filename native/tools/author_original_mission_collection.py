#!/usr/bin/env python3
"""Build authentic multi-mission native game sources from live Level captures.

Each entry is a complete immutable Level + Stage + authored mission contract.
Reuse author_original_unity_level.py for the actual scene/gameplay authoring:
there is no second physics, weapon, AI or campaign serializer.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

from author_original_unity_level import author
from native_training_artifacts import atomic_json, file_sha256, sync_directory


def load_manifest(path: Path) -> dict:
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if (not isinstance(manifest, dict) or
            set(manifest) != {"schema_version", "physics_matrix", "missions"} or
            manifest["schema_version"] != 1 or
            not isinstance(manifest["physics_matrix"], str) or
            not manifest["physics_matrix"] or
            not isinstance(manifest["missions"], list) or
            not 1 <= len(manifest["missions"]) <= 128):
        raise ValueError("Original campaign collection needs source manifest schema 1 and 1..128 missions")
    required = {"source", "stage_palette", "seed", "max_rounds"}
    optional = {"level_contract", "hivemind_config", "ai_tactics",
                "broadphase_cell_size", "physics_workers", "navigation_workers",
                "deferred_hivemind"}
    for index, mission in enumerate(manifest["missions"], start=1):
        if (not isinstance(mission, dict) or
                not required.issubset(mission) or
                set(mission) - required - optional or
                any(not isinstance(mission[field], str) or not mission[field]
                    for field in ("source", "stage_palette")) or
                any(field in mission and
                    (not isinstance(mission[field], str) or not mission[field])
                    for field in ("level_contract", "hivemind_config")) or
                ("deferred_hivemind" in mission and
                 type(mission["deferred_hivemind"]) is not bool)):
            raise ValueError(f"Original mission {index} lacks a required physical source or has unknown fields")
    return manifest


def author_collection(source_manifest: Path, destination: Path) -> dict:
    source_manifest = source_manifest.resolve(strict=True)
    manifest_sha = file_sha256(source_manifest)
    configuration = load_manifest(source_manifest)
    base = source_manifest.parent
    absolute = lambda name: (base / name).resolve(strict=True)
    collision_matrix = absolute(configuration["physics_matrix"])
    destination = destination.resolve()
    if (not destination.parent.is_dir() or
            destination.exists() or destination.is_symlink()):
        raise FileExistsError("Original native campaign collection requires a fresh destination")
    authored = []
    with tempfile.TemporaryDirectory(
        prefix=f".{destination.name}.native-collection-",
        dir=destination.parent,
    ) as directory:
        stage = Path(directory) / "complete"
        stage.mkdir()
        for index, mission in enumerate(configuration["missions"], start=1):
            script = mission.get("ai_tactics")
            if script is not None and script not in (
                "aggressive", "circle", "right-swipe", "left-swipe",
                "in-and-out", "mixed",
            ):
                raise ValueError(f"Original mission {index}: unknown native AI tactic")
            run_name = f"mission-{index:03d}"
            original = absolute(mission["source"])
            result = author(
                stage / run_name,
                original,
                collision_matrix,
                absolute(mission["stage_palette"]),
                absolute(mission["level_contract"])
                    if mission.get("level_contract") else None,
                mission["seed"],
                mission["max_rounds"],
                mission.get("broadphase_cell_size", 20),
                mission.get("physics_workers"),
                mission.get("navigation_workers"),
                absolute(mission["hivemind_config"])
                    if mission.get("hivemind_config") else None,
                script,
                deferred_hivemind=mission.get("deferred_hivemind", False),
            )
            authored.append({
                "mission_index": index,
                "run": f"{run_name}/run.json",
                "run_sha256": result["native_run_sha256"],
                "scene": f"{run_name}/source-level.json",
                "scene_sha256": result["native_scene_sha256"],
                "source_original_level": result["original_level"],
                "source_original_stage": result["original_stage"],
                "source_receipt": f"{run_name}/original-source-receipt.json",
                "source_receipt_sha256": file_sha256(
                    stage / run_name / "original-source-receipt.json"
                ),
            })
        if file_sha256(source_manifest) != manifest_sha:
            raise ValueError("Original campaign collection source manifest changed during authoring")
        # Publish these actual original battles into the EXISTING native
        # client library, rather than requiring a separate launcher. These
        # are individual physical mission captures, not a fabricated
        # replacement campaign ledger: the source campaign author
        # independently handles story progression and career persistence.
        # The game-library reader accepts at most 100 entries per file.
        library_files = []
        for first in range(0, len(authored), 100):
            segment = authored[first:first + 100]
            entries = []
            for row in segment:
                title = " ".join(
                    str(row["source_original_level"]).split()
                )
                if not title or any(ord(ch) < 32 for ch in title):
                    raise ValueError("Original Level name cannot be displayed in native library")
                entries.append({
                    "id": f"original-encounter-{row['mission_index']:03d}",
                    "title": title[:120],
                    "kind": "battle",
                    "config": row["run"],
                })
            filename = ("native-library.json" if first == 0
                        else f"native-library-{first // 100 + 1:03d}.json")
            atomic_json(stage / filename, {
                "library_schema": 1, "games": entries,
            })
            library_files.append({
                "path": filename,
                "sha256": file_sha256(stage / filename),
                "game_count": len(entries),
                "first_mission_index": segment[0]["mission_index"],
            })
        suite = {
            "schema_version": 1,
            "kind": "native_original_campaign_source_collection",
            "source_manifest_sha256": manifest_sha,
            "source_manifest": str(source_manifest),
            "physics_matrix_sha256": file_sha256(collision_matrix),
            "missions": authored,
            "native_game_libraries": library_files,
        }
        # No partial collection or library becomes published; the complete
        # source Run set and its menu indexes are committed atomically.
        atomic_json(stage / "collection-receipt.json", suite)
        sync_directory(stage)
        if destination.exists() or destination.is_symlink():
            raise FileExistsError("Original campaign collection destination was claimed")
        os.rename(stage, destination)
        sync_directory(destination.parent)
        return suite


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path,
                        help="original source capture manifest with per-mission physical settings")
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    print(json.dumps(author_collection(args.manifest, args.output_directory),
                     sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
