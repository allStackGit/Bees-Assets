#!/usr/bin/env python3
"""Compose either Titania obstacle field into an ordinary native scene snapshot."""

import argparse
import json
import math
import re
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]
OBSTACLES = ROOT / "Resources/Obstacles"
FIRE_TANK = ROOT / "Prefabs/Entities/Objects/Fire Tank.prefab"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unity_documents(path):
    source = path.read_text(encoding="utf-8-sig")
    source = re.sub(r"^%.*\n", "", source, flags=re.MULTILINE)
    source = re.sub(
        r"^--- !u!\d+ &(-?\d+)(?: stripped)?$", r"---\nfile_id: \1",
        source, flags=re.MULTILINE,
    )
    documents = {}
    for doc in yaml.safe_load_all(source):
        file_id = doc.pop("file_id")
        require(len(doc) == 1 and file_id not in documents, f"{path}: invalid document {file_id}")
        documents[file_id] = next(iter(doc.items()))
    return documents


def only_document(documents, kind, predicate=lambda _: True):
    matches = [(file_id, body) for file_id, (key, body) in documents.items()
               if key == kind and predicate(body)]
    require(len(matches) == 1, f"expected one {kind}, found {len(matches)}")
    return matches[0]


def vector(x, y):
    return {"x": x, "y": y}


class Prefab:
    def __init__(self, path):
        self.documents = unity_documents(path)
        self.guid = re.search(r"^guid: ([a-f0-9]{32})$", Path(str(path) + ".meta").read_text(),
                              re.MULTILINE)[1]
        self.transform_id, self.transform = only_document(
            self.documents, "Transform", lambda body: body.get("m_Father") == {"fileID": 0},
        )
        self.root_id = self.transform["m_GameObject"]["fileID"]
        self.root = self.documents[self.root_id][1]
        require(self.root["m_IsActive"] == 1, f"{path}: inactive root")

    def placement(self, instance, documents):
        overrides = {}
        modification = instance["m_Modification"]
        require(not modification["m_RemovedComponents"] and not modification["m_RemovedGameObjects"],
                "a layout instance removed prefab contents")
        for component in modification["m_AddedComponents"]:
            added = documents[component["addedObject"]["fileID"]][0]
            require(added == "SpriteRenderer", f"unsupported added component {added}")
        for entry in modification["m_Modifications"]:
            target = entry["target"]["fileID"]
            prop = entry["propertyPath"]
            require(entry["target"]["guid"] == self.guid, "unexpected override prefab")
            kind, body = self.documents[target]
            if kind in ("SpriteRenderer", "SpriteMask") or prop == "m_Name":
                continue
            if prop.startswith("m_SpriteTilingProperty.") and body.get("m_AutoTiling") == 0:
                continue
            if target == self.transform_id and prop.startswith("m_LocalEulerAnglesHint."):
                continue
            if target == self.transform_id and prop.rsplit(".", 1)[0] in (
                "m_LocalPosition", "m_LocalRotation", "m_LocalScale",
            ):
                overrides[prop] = float(entry["value"])
                continue
            require(kind == "MonoBehaviour" and "TargetObstacle" in body and prop == "TargetObstacle",
                    f"unsupported {kind} override {prop}")
            overrides[prop] = entry["objectReference"]["fileID"]

        def axis(field, name):
            return overrides.get(f"{field}.{name}", self.transform[field][name])

        x, y = (axis("m_LocalPosition", name) for name in "xy")
        sx, sy = (axis("m_LocalScale", name) for name in "xy")
        qx, qy, qz, qw = (axis("m_LocalRotation", name) for name in "xyzw")
        require(all(map(math.isfinite, (x, y, sx, sy, qx, qy, qz, qw))) and
                sx != 0 and sy != 0 and abs(qx) < 1e-5 and abs(qy) < 1e-5 and
                abs(qz*qz + qw*qw - 1) < 0.0003, "unsupported layout transform")
        angle = math.degrees(2 * math.atan2(qz, qw))
        return vector(x, y), (sx, sy), (angle + 180) % 360 - 180, overrides.get("TargetObstacle")

    def collider(self, is_trigger):
        component_id, component = only_document(
            self.documents, "MonoBehaviour", lambda body: "Collider" in body,
        )
        kind, collider = self.documents[component["Collider"]["fileID"]]
        require(collider["m_GameObject"]["fileID"] == self.root_id and
                collider["m_IsTrigger"] == is_trigger and collider["m_Enabled"] == 1 and
                collider["m_AutoTiling"] == 0, "expected the authored enabled root collider")
        return component_id, component, kind, collider


def compose(scene, name):
    box = Prefab(OBSTACLES / "_Obstacle Prefab.prefab")
    background = Prefab(OBSTACLES / "_Obstacle Background Prefab.prefab")
    tank = Prefab(FIRE_TANK)
    box_component, wall, box_kind, rectangle = box.collider(is_trigger=0)
    _, explosive, tank_kind, polygon = tank.collider(is_trigger=1)
    require(box_kind == "BoxCollider2D" and rectangle["m_EdgeRadius"] == 0 and
            wall["ObstacleType"] == 0 and tank_kind == "PolygonCollider2D" and
            len(polygon["m_Points"]["m_Paths"]) == 1, "unsupported Titania colliders")
    hx, hy = (rectangle["m_Size"][axis] / 2 for axis in "xy")
    box_points = [vector(-hx, -hy), vector(hx, -hy), vector(hx, hy), vector(-hx, hy)]

    layout = Prefab(OBSTACLES / f"{name}.prefab")
    layout_position = layout.transform["m_LocalPosition"]
    require(layout.transform["m_LocalRotation"] == {"x": 0, "y": 0, "z": 0, "w": 1} and
            all(layout.transform["m_LocalScale"][axis] == 1 for axis in "xy"),
            "expected identity rotation and unit scale at the layout root")
    (background_transform,) = layout.transform["m_Children"]
    wrapper_transform = layout.documents[background_transform["fileID"]][1]
    wrapper_id = wrapper_transform["m_PrefabInstance"]["fileID"]
    wrapper = layout.documents[wrapper_id][1]
    require(wrapper["m_SourcePrefab"]["guid"] == background.guid and
            wrapper["m_Modification"]["m_TransformParent"]["fileID"] == layout.transform_id,
            "unexpected obstacle hierarchy")
    origin, scale, angle, _ = background.placement(wrapper, layout.documents)
    require(origin == vector(0, 0) and scale == (1, 1) and angle == 0,
            "expected an identity layout container")
    require(not scene.get("has_policy_episode") and not scene.get("map_objects") and
            all(o["prefab_name"] != box.root["m_Name"] for o in scene["obstacles"]),
            "expected an ordinary snapshot without a Titania field")
    next_id = 1 + max((record["entity_id"] for key in ("ships", "obstacles")
                       for record in scene[key]), default=0)
    walls, tanks, wall_ids = [], [], {}
    consumed = {wrapper_id}
    for child in wrapper["m_Modification"]["m_AddedGameObjects"]:
        transform = layout.documents[child["addedObject"]["fileID"]][1]
        instance_id = transform["m_PrefabInstance"]["fileID"]
        instance = layout.documents[instance_id][1]
        require(child["insertIndex"] == -1 and instance_id not in consumed and
                instance["m_Modification"]["m_TransformParent"] == background_transform and
                not instance["m_Modification"]["m_AddedGameObjects"], "unexpected layout child")
        consumed.add(instance_id)
        guid = instance["m_SourcePrefab"]["guid"]
        require(guid in (box.guid, tank.guid), "unsupported physical prefab in Titania field")
        is_wall = guid == box.guid
        prefab, collider = (box, rectangle) if is_wall else (tank, polygon)
        position, (sx, sy), rotation, link = prefab.placement(instance, layout.documents)
        position = vector(position["x"] + layout_position["x"],
                          position["y"] + layout_position["y"])
        offset = collider["m_Offset"]
        points = box_points if is_wall else polygon["m_Points"]["m_Paths"][0]
        vertices = [vector((point["x"] + offset["x"]) * sx, (point["y"] + offset["y"]) * sy)
                    for point in points]
        record = dict(entity_id=next_id, position=position, rotation_degrees=rotation,
                      layer=prefab.root["m_Layer"], collider_vertices=vertices,
                      angular_velocity_degrees=0)
        if is_wall:
            health = wall["OriginalHealth"] if wall["OriginalHealth"] > 0 else wall["Health"]
            walls.append(dict(record, kind="box", prefab_name=box.root["m_Name"],
                              health=health, effective_max_health=health, prefab_number=0,
                              speed=0, is_shard=False, velocity=vector(0, 0)))
            wall_ids[instance_id] = next_id
        else:
            tanks.append((link, dict(record, kind="fire-tank", collider_kind="polygon",
                                     health=explosive["MaxHealth"], max_health=explosive["MaxHealth"],
                                     linear_velocity=vector(0, 0), circle_offset=vector(0, 0), circle_radius=0,
                                     explosion_power=explosive["Power"])))
        next_id += 1

    require(consumed == {key for key, (kind, _) in layout.documents.items() if kind == "PrefabInstance"},
            "the layout contains instances outside the obstacle container")
    links = {}
    for file_id, (kind, body) in layout.documents.items():
        if kind == "MonoBehaviour" and body.get("m_CorrespondingSourceObject") == {
            "fileID": box_component, "guid": box.guid, "type": 3,
        }:
            links[file_id] = wall_ids[body["m_PrefabInstance"]["fileID"]]
    targets = [links.get(link) for link, _ in tanks]
    if None in targets or len(set(targets)) != len(targets):
        # Titania I repairs duplicated links in hierarchy order by nearest unused wall.
        require(len(walls) >= len(tanks), "insufficient demolition targets")
        remaining = walls.copy()
        targets = []
        for _, tank_record in tanks:
            pos = tank_record["position"]
            nearest = min(remaining, key=lambda wall: math.dist(
                (pos["x"], pos["y"]), (wall["position"]["x"], wall["position"]["y"]),
            ))
            targets.append(nearest["entity_id"])
            remaining.remove(nearest)

    scene["obstacles"].extend(walls)
    scene["map_objects"] = [dict(record, linked_obstacle_id=target)
                            for (_, record), target in zip(tanks, targets, strict=True)]
    scene["has_obstacles"] = True
    return len(walls), len(tanks)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("field", choices=("Minesweeper", "Bee-noculars"))
    parser.add_argument("scene", type=Path, help="ordinary physical snapshot containing the fleets")
    parser.add_argument("output", type=Path, help="new composed physical snapshot")
    args = parser.parse_args()
    try:
        scene = json.loads(args.scene.read_text(encoding="utf-8-sig"))
        walls, tanks = compose(scene, args.field)
        with args.output.open("x", encoding="utf-8") as out:
            json.dump(scene, out, allow_nan=False, indent=2)
            out.write("\n")
    except (KeyError, TypeError, ValueError, OSError, yaml.YAMLError) as exc:
        parser.exit(1, f"{args.field}: {exc}\n")
    print(f"Imported {walls} walls and {tanks} Fire Tanks into {args.output}.")


if __name__ == "__main__":
    main()
