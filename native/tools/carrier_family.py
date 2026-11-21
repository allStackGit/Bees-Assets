"""Source Carrier family geometry and grouping for native scene publishers.

These helpers only read authored definitions; the Rust Level remains the
sole constructor of live ships, weapons, and campaign ownership.
"""


def squad_members(squad: dict) -> list[int]:
    return squad["member_ids"] if "member_ids" in squad else squad["members"]


def has_policy(ship: dict) -> bool:
    return (ship.get("policy") is not None or
            ship.get("has_bound_policy") is True or
            ship.get("is_rl_policy_controlled") is True)


def carrier_parent_id(ship: dict) -> int | None:
    if ship["ship_type"] not in (8, 19):
        return None
    if "parent_carrier_id" in ship:
        parent = ship["parent_carrier_id"]
        return parent if type(parent) is int and parent > 0 else None
    if ship["ship_type"] == 8:
        return ship.get("drone_carrier_id")
    if ship["ship_type"] == 19:
        special = ship.get("special")
        if isinstance(special, dict) and special.get("kind") == "striker":
            return special.get("carrier_id") or None
    return None


def source_carrier_groups(scene: dict, parent: dict, *, inert: bool = False):
    """Return the actual single-parent, single-class child squads in order.

    A bare hull, mixed squad, or incomplete/missing family is not a
    purchase blueprint. Never infer a missing deck count from ship type.
    """
    if parent["ship_type"] != 5 or has_policy(parent):
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
        members = squad_members(squad)
        if not any(member in children for member in members):
            continue
        # The Rust campaign installer accepts live Carrier decks only
        # under the original player command owner, and reserved decks only
        # as inactive (non-player) templates. Do not publish a shop class
        # that the playable native campaign cannot actually construct.
        if (squad["side"] != parent["side"] or
                squad.get("saved_squad_id") not in (None, 0) or
                not squad.get("is_carrier_squad") or
                squad.get("user_controlled") is not (not inert) or
                (inert and (squad.get("can_accept_input") or
                            squad.get("locked_on") or
                            squad.get("hotkey_number") is not None or
                            squad.get("hivemind") is not None)) or
                len(set(members)) != len(members) or
                not all(member in children for member in members)):
            return None
        hulls = [children.pop(member) for member in members]
        if any(ship["side"] != parent["side"] or
               ship["ship_type"] != hulls[0]["ship_type"] or
               ship.get("persistent_fleet") or has_policy(ship) or
               ship["health"] != ship["max_health"] or ship.get("mined_cargo", 0)
               for ship in hulls):
            return None
        groups.append((squad, hulls))
    return groups if not children else None
