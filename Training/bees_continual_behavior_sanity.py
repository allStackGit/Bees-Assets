"""Conservative catastrophic-behavior checks for continual-learning evaluation reports.

This module deliberately avoids judging strategy style. A non-shooting policy can pass when it
wins (for example through ramming), while a policy that shows no combat activity or success at
all, or times out every authoritative match, is rejected.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, Optional, Sequence

from bees_continual_learning import ContinualLearningError, ValidationError

BEHAVIOR_SANITY_SCHEMA_VERSION = 1


_SUMMARY_INTEGER_FIELDS = (
    "matches",
    "wins",
    "losses",
    "draws",
    "timeouts",
    "candidate_starting_tsv",
    "candidate_final_tsv",
    "candidate_shots",
    "candidate_hits",
    "candidate_damage",
)


def _nonnegative_int(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise ValidationError(f"{label} must be a non-negative integer.")
    return value


def _validated_candidate_summary(value: object, label: str) -> Dict[str, int]:
    if not isinstance(value, Mapping):
        raise ValidationError(f"{label} must be an object.")
    summary = {
        field: _nonnegative_int(value.get(field), f"{label}.{field}")
        for field in _SUMMARY_INTEGER_FIELDS
    }
    if summary["wins"] + summary["losses"] + summary["draws"] != summary["matches"]:
        raise ValidationError(f"{label} outcome counts do not equal matches.")
    if summary["timeouts"] > summary["draws"]:
        raise ValidationError(f"{label}.timeouts cannot exceed draws.")
    if summary["matches"] > 0 and summary["candidate_starting_tsv"] <= 0:
        raise ValidationError(f"{label} requires positive candidate starting TSV evidence.")
    if (summary["candidate_hits"] == 0) != (summary["candidate_damage"] == 0):
        raise ValidationError(f"{label} candidate hit/damage counters are inconsistent.")
    return summary


def _candidate_summaries(report: Mapping[str, Any]) -> Iterable[tuple[str, Mapping[str, Any]]]:
    champion = report.get("candidate_vs_champion")
    if champion is not None:
        if not isinstance(champion, Mapping):
            raise ValidationError("candidate_vs_champion must be an object or null.")
        yield "candidate_vs_champion", champion

    historical = report.get("historical", [])
    if not isinstance(historical, list):
        raise ValidationError("historical must be a list.")
    for index, item in enumerate(historical):
        if not isinstance(item, Mapping):
            raise ValidationError(f"historical[{index}] must be an object.")
        summary = item.get("candidate_summary")
        if summary is None:
            raise ValidationError(f"historical[{index}].candidate_summary is required.")
        if not isinstance(summary, Mapping):
            raise ValidationError(f"historical[{index}].candidate_summary must be an object.")
        yield f"historical[{index}].candidate_summary", summary

    competencies = report.get("competencies", [])
    if not isinstance(competencies, list):
        raise ValidationError("competencies must be a list.")
    for index, item in enumerate(competencies):
        if not isinstance(item, Mapping):
            raise ValidationError(f"competencies[{index}] must be an object.")
        summary = item.get("summary")
        if summary is None:
            raise ValidationError(f"competencies[{index}].summary is required.")
        if not isinstance(summary, Mapping):
            raise ValidationError(f"competencies[{index}].summary must be an object.")
        yield f"competencies[{index}].summary", summary


def assess_behavior_sanity(report: Mapping[str, Any]) -> Dict[str, Any]:
    """Return auditable evidence for conservative catastrophic-behavior rejection.

    The check is intentionally a floor, not a preferred-strategy metric. It rejects only when
    authoritative telemetry is unavailable, no candidate matches exist, every candidate match
    times out, or there is no observable candidate combat activity/success across all groups.
    A candidate win counts as success so ramming or other valid non-shooting strategies are not
    rejected merely because weapon counters are zero.
    """
    if not isinstance(report, Mapping):
        raise ValidationError("Evaluation report must be an object.")

    evaluator = report.get("evaluator")
    if not isinstance(evaluator, Mapping):
        raise ValidationError("Evaluation report evaluator evidence is required.")
    authoritative_runner = evaluator.get("authoritative_match_runner") is True
    telemetry_validated = evaluator.get("authoritative_telemetry_validated") is True

    totals = {field: 0 for field in _SUMMARY_INTEGER_FIELDS}
    groups = 0
    for label, raw in _candidate_summaries(report):
        summary = _validated_candidate_summary(raw, label)
        groups += 1
        for field in _SUMMARY_INTEGER_FIELDS:
            totals[field] += summary[field]

    matches = totals["matches"]
    combat_activity_or_success = (
        totals["candidate_shots"] > 0
        or totals["candidate_hits"] > 0
        or totals["candidate_damage"] > 0
        or totals["wins"] > 0
    )
    all_matches_timed_out = matches > 0 and totals["timeouts"] == matches

    checks = {
        "authoritative_match_runner": authoritative_runner,
        "authoritative_telemetry_validated": telemetry_validated,
        "candidate_matches_present": matches > 0,
        "combat_activity_or_success_observed": combat_activity_or_success,
        "not_all_matches_timed_out": not all_matches_timed_out,
    }
    reasons: List[str] = []
    if not authoritative_runner:
        reasons.append("evaluation did not use the authoritative match runner")
    if not telemetry_validated:
        reasons.append("authoritative evaluation telemetry was not fully validated")
    if matches <= 0:
        reasons.append("no candidate evaluation matches were available")
    if matches > 0 and not combat_activity_or_success:
        reasons.append("candidate showed no weapon activity, damage, or wins in any evaluated match")
    if all_matches_timed_out:
        reasons.append("every candidate evaluation match timed out")

    return {
        "schema_version": BEHAVIOR_SANITY_SCHEMA_VERSION,
        "passed": not reasons,
        "checks": checks,
        "reasons": reasons,
        "match_groups": groups,
        "totals": totals,
    }


def apply_behavior_sanity(report: Mapping[str, Any]) -> Dict[str, Any]:
    """Copy an evaluation report and attach behavior evidence plus the derived gate boolean."""
    evidence = assess_behavior_sanity(report)
    updated = dict(report)
    evaluator = dict(updated.get("evaluator", {}))
    evaluator["behavior_sanity"] = evidence
    updated["evaluator"] = evaluator
    updated["behavior_sanity_passed"] = bool(evidence["passed"])
    return updated


def _load_report(path: Path) -> Mapping[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"Evaluation report does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Evaluation report is invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, Mapping):
        raise ValidationError("Evaluation report must contain a JSON object.")
    return value


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Assess catastrophic behavioral sanity from an authoritative evaluation report."
    )
    parser.add_argument("report", help="Evaluation report JSON path.")
    args = parser.parse_args(argv)
    try:
        evidence = assess_behavior_sanity(_load_report(Path(args.report)))
        print(json.dumps(evidence, indent=2, sort_keys=True))
        return 0 if evidence["passed"] else 2
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Behavior sanity assessment failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
