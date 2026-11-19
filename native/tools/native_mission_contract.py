"""Stable semantic contract for native goals in v26's reserved policy block.

The actor's tensor width is unchanged. A shape-only check cannot distinguish
a stale collector that encodes objective features differently. Both local and
distributed collectors therefore attest the exact slot layout in the same
rollout receipt that records physically successful decision rows.
"""

ENCODING_VERSION = 1
OBJECTIVE_START = (
    25 + 12 + 41 + 64 * 45 + 64 * 41 + 5 * 15 +
    8 * 7 + 64 * 12 + 48 * 11
)
OBJECTIVE_WIDTH = 16
MISSION_KEYS = ("survival", "demolition", "escort", "extraction")
COUNT_KEYS = (
    "rows_encoded", "native_mission_rows", "allies_encoded",
    "enemies_encoded", "map_objects_encoded",
)
CONTRACT_KEYS = (
    "native_mission_encoding_version", "native_mission_objective_start",
    "native_mission_objective_width",
)


def level_has_goal(level):
    """A null/omitted optional objective is not an authored mission."""
    return any(level.get(key) is not None for key in MISSION_KEYS)


def verified_policy_observations(work, *, require_goal=False):
    """Validate native producer evidence without inferring missing work.

    Existing non-mission collectors may lack this metric. Mission runs
    require both positive goal-bearing rows and an explicit semantic layout
    identity. No automatic compatibility fallback is safe for MA-POCA.
    """
    if work is None:
        if require_goal:
            raise ValueError("native mission collector has no objective observation evidence")
        return None
    if not isinstance(work, dict) or any(
        type(work.get(key)) is not int or work[key] < 0
        for key in COUNT_KEYS
    ):
        raise ValueError("native policy observation work has invalid counters")
    if work["native_mission_rows"] > work["rows_encoded"]:
        raise ValueError("native mission feature count exceeds encoded rows")
    if (require_goal or work["native_mission_rows"] > 0 or
            any(key in work for key in CONTRACT_KEYS)):
        if (work.get(CONTRACT_KEYS[0]) != ENCODING_VERSION or
                work.get(CONTRACT_KEYS[1]) != OBJECTIVE_START or
                work.get(CONTRACT_KEYS[2]) != OBJECTIVE_WIDTH):
            raise ValueError("native mission feature layout differs from the learner contract")
    if require_goal and work["native_mission_rows"] == 0:
        raise ValueError("native mission collector emitted no goal-bearing observations")
    return work
