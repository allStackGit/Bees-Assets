"""Source Carrier family geometry and grouping for native scene publishers.

These helpers only read authored definitions; the Rust Level remains the
sole constructor of live ships, weapons, and campaign ownership.
"""


def carrier_parent_id(ship: dict) -> int | None:
    if ship["ship_type"] == 8:
        return ship.get("drone_carrier_id")
    if ship["ship_type"] == 19:
        special = ship.get("special")
        if isinstance(special, dict) and special.get("kind") == "striker":
            return special.get("carrier_id")
    return None


def source_carrier_groups(scene: dict, parent: dict, *, inert: bool = False):
    """Return the actual single-parent, single-class child squads in order.

    A bare hull, mixed squad, or incomplete/missing family is not a
    purchase blueprint. Never infer a missing deck count from ship type.
    """
    if parent["ship_type"] != 5 or parent.get("policy") is not None:
        return None
    ships = scene.get("fleet_templates" if inert else "ships", ())
    squads = scene.get("fleet_template_squads" if inert else "squads", ())
    children = {
        ship["entity_id"]: ship for ship in ships
        if carrier_parent_id(ship) == parent["entity_id"]
    }
    if not children:
        return None
    groups = []
    for squad in squads:
        members = squad["members"]
        if not any(member in children for member in members):
            continue
        if (squad["side"] != parent["side"] or
                squad.get("saved_squad_id") is not None or
                not squad.get("is_carrier_squad") or
                not all(member in children for member in members)):
            return None
        hulls = [children.pop(member) for member in members]
        if any(ship["side"] != parent["side"] or
               ship["ship_type"] != hulls[0]["ship_type"] or
               ship.get("persistent_fleet") or ship.get("policy") is not None or
               ship["health"] != ship["max_health"] or ship.get("mined_cargo", 0)
               for ship in hulls):
            return None
        groups.append((squad, hulls))
    return groups if not children else None
