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
COVERAGE_KEYS = (
    "native_mission_rows_by_kind", "native_mission_visible_target_rows",
    "native_mission_destination_rows",
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
    mission_rows = work["native_mission_rows"]
    if mission_rows > work["rows_encoded"]:
        raise ValueError("native mission feature count exceeds encoded rows")
    if any(key in work for key in COVERAGE_KEYS):
        if not all(key in work for key in COVERAGE_KEYS):
            raise ValueError("partial native mission feature coverage report")
        kinds = work["native_mission_rows_by_kind"]
        if (not isinstance(kinds, dict) or set(kinds) != set(MISSION_KEYS) or
                any(type(kinds[key]) is not int or kinds[key] < 0
                    for key in MISSION_KEYS) or
                sum(kinds.values()) != mission_rows):
            raise ValueError("native per-mission feature counts do not reconcile")
        for key in COVERAGE_KEYS[1:]:
            if type(work[key]) is not int or not 0 <= work[key] <= mission_rows:
                raise ValueError("native goal target/destination coverage exceeds real policy rows")
    if (require_goal or work["native_mission_rows"] > 0 or
            any(key in work for key in CONTRACT_KEYS)):
        if (work.get(CONTRACT_KEYS[0]) != ENCODING_VERSION or
                work.get(CONTRACT_KEYS[1]) != OBJECTIVE_START or
                work.get(CONTRACT_KEYS[2]) != OBJECTIVE_WIDTH):
            raise ValueError("native mission feature layout differs from the learner contract")
    if require_goal and work["native_mission_rows"] == 0:
        raise ValueError("native mission collector emitted no goal-bearing observations")
    return work

def reconciled_mission_contract(work_samples):
    """Keep one verified objective encoding identity across process/machine sums.

    The observations represent overlapping independent collectors, but
    their reserved tensor channels cannot represent different contracts
    within one optimizer update. Older non-mission workers may lack the
    feature signature; they are admissible only when they emitted zero
    native mission rows and the signed source permits that.
    """
    attested = [sample for sample in work_samples
                if all(field in sample for field in CONTRACT_KEYS)]
    if not attested:
        return {}
    reference = {field: attested[0][field] for field in CONTRACT_KEYS}
    if any(any(sample[field] != reference[field] for field in CONTRACT_KEYS)
           for sample in attested):
        raise ValueError("native rollout workers disagree on mission feature semantics")
    if any(sample["native_mission_rows"] > 0 and
           not all(field in sample for field in CONTRACT_KEYS)
           for sample in work_samples):
        raise ValueError("unattested mission observations cannot enter a shared rollout")
    return reference



def combined_policy_observations(observations):
    """Aggregate only counts observed by every worker, preserving one ABI.

    A mixed old/new rollout must not turn unavailable historical target
    discovery statistics into measured zeros. Per-kind evidence is complete
    only when every collector exposed the same source-owned counters.
    """
    for work in observations:
        verified_policy_observations(work)
    aggregate = {
        key: sum(work[key] for work in observations)
        for key in COUNT_KEYS
    }
    aggregate.update(reconciled_mission_contract(observations))
    if all(all(key in work for key in COVERAGE_KEYS) for work in observations):
        aggregate["native_mission_rows_by_kind"] = {
            kind: sum(work["native_mission_rows_by_kind"][kind]
                      for work in observations)
            for kind in MISSION_KEYS
        }
        for key in COVERAGE_KEYS[1:]:
            aggregate[key] = sum(work[key] for work in observations)
    verified_policy_observations(aggregate)
    return aggregate
