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
import re
import subprocess
import sys
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


# Original human Campaign and Challenge are not just twelve separate
# skirmishes. This bridge calls the EXISTING native campaign publisher,
# which owns the real story, fleet ledger, checkpoint recovery and grants.
# An explicit source-authored options file supplies all additional class
# purchases, rewards, mode unlocks and route variants; no game progress is
# invented here.
CAMPAIGN_VALUE_FLAGS = frozenset({
    "--shop-types", "--grant-only-types", "--mission-requires",
    "--mission-advance", "--mission-grant-on", "--mission-bank-on",
    "--mission-unlock-any", "--mission-codex",
    "--mission-scientists-met", "--postcampaign-game",
    "--training-room-game", "--mission-variant",
    "--mission-defeat-variant", "--shop-unlock", "--max-squad-ships",
    "--victory-grant", "--victory-squad",
})
CAMPAIGN_BOOL_FLAGS = frozenset({"--persist-enemy-losses"})


def publish_original_campaign(collection: Path, options_file: Path) -> dict:
    """Package real captured missions under the canonical native career."""
    collection = collection.resolve(strict=True)
    options_file = options_file.resolve(strict=True)
    if (collection / "original-campaign").exists():
        raise FileExistsError("original campaign is already published")
    receipt_path = collection / "collection-receipt.json"
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    original = json.loads(options_file.read_text(encoding="utf-8"))
    if (not isinstance(original, dict) or
            set(original) != {"schema_version", "title", "arguments"} or
            original["schema_version"] != 1 or
            not isinstance(original["title"], str) or
            not 1 <= len(original["title"].strip()) <= 80 or
            not isinstance(original["arguments"], list) or
            len(original["arguments"]) > 120):
        raise ValueError("source campaign options must select a title and approved catalog options")
    arguments = original["arguments"]
    selected = []
    position = 0
    while position < len(arguments):
        flag = arguments[position]
        if type(flag) is not str:
            raise ValueError("original campaign option names must be strings")
        if flag in CAMPAIGN_BOOL_FLAGS:
            selected.append(flag)
            position += 1
        elif flag in CAMPAIGN_VALUE_FLAGS:
            if (position + 1 >= len(arguments) or
                    type(arguments[position + 1]) is not str or
                    not arguments[position + 1] or
                    arguments[position + 1].startswith("--")):
                raise ValueError(f"original campaign value is missing for {flag}")
            selected.extend((flag, arguments[position + 1]))
            position += 2
        else:
            raise ValueError(f"unsupported original campaign option: {flag}")
    missions = receipt.get("missions")
    if not isinstance(missions, list) or len(missions) != 12:
        raise ValueError("full original campaign requires all twelve source missions")
    catalog_file = (
        Path(__file__).resolve().parents[2] /
        "Scripts/Levels/CampaignMissionCatalog.cs"
    )
    definitions = [
        (int(index), name, int(map_index))
        for index, name, map_index in re.findall(
            r'new MissionDefinition\(\s*(\d+)\s*,\s*"([^"]+)"\s*,\s*(\d+)',
            catalog_file.read_text(encoding="utf-8"),
        )
    ]
    if (len(definitions) != 12 or
            [index for index, _, _ in definitions] != list(range(12))):
        raise ValueError("original Unity campaign level catalog has changed")
    mission_paths = []
    for index, row in enumerate(missions):
        path = (collection / row["run"]).resolve(strict=True)
        physical = (collection / row["scene"]).resolve(strict=True)
        if (not path.is_relative_to(collection) or
                not physical.is_relative_to(collection) or
                file_sha256(path) != row["run_sha256"] or
                file_sha256(physical) != row["scene_sha256"]):
            raise ValueError("original campaign Level changed since source collection")
        snapshot = json.loads(physical.read_text(encoding="utf-8"))
        mission_id, name, map_index = definitions[index]
        if (snapshot.get("source_level_options_id") != mission_id or
                snapshot.get("source_level_options_name") != name or
                snapshot.get("source_level_map_index") != map_index or
                (snapshot.get("targeting_context") or {}).get("user_side") != 2 or
                (snapshot.get("targeting_context") or {}).get("has_player") is not True):
            raise ValueError(
                f"source campaign Level {index + 1} is not the original "
                "persisted Human-side mission/map in source catalog order"
            )
        mission_paths.append(path)

    publisher = Path(__file__).resolve().with_name("author_campaign.py")
    published = collection / "original-campaign"
    command = [
        sys.executable, str(publisher), str(published),
        "--original-campaign", "--player-side", "2",
        "--title", original["title"], "--mission",
        str(mission_paths[0]),
    ]
    for path in mission_paths[1:]:
        command.extend(("--mission", str(path)))
    command.extend(selected)
    # The existing author verifies the entire story/mission contract,
    # publishes self-contained source assets and refuses unfinished
    # native FleetShip lineage. Do not replace it with another campaign.
    subprocess.run(command, check=True)
    catalog_path = published / "campaign.json"
    library_path = published / "library.json"
    if not catalog_path.is_file() or not library_path.is_file():
        raise ValueError("original native campaign publisher did not commit its catalog")
    result = {
        "schema_version": 1,
        "kind": "native_original_campaign_source_collection_published",
        "collection_receipt_sha256": file_sha256(receipt_path),
        "source_options_sha256": file_sha256(options_file),
        "source_catalog_sha256": file_sha256(catalog_file),
        "campaign": "original-campaign/campaign.json",
        "campaign_sha256": file_sha256(catalog_path),
        "library": "original-campaign/library.json",
        "library_sha256": file_sha256(library_path),
        "missions": len(mission_paths),
    }
    atomic_json(collection / "campaign-link-receipt.json", result)
    sync_directory(collection)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path,
                        help="original source capture manifest with per-mission physical settings")
    parser.add_argument("output_directory", type=Path)
    parser.add_argument("--original-campaign-options", type=Path,
                        help="source-approved original campaign options JSON; "
                             "publishes the twelve real missions using author_campaign.py")
    args = parser.parse_args()
    result = author_collection(args.manifest, args.output_directory)
    if args.original_campaign_options is not None:
        result["original_campaign"] = publish_original_campaign(
            args.output_directory, args.original_campaign_options,
        )
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
