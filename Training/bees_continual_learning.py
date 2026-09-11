"""Central continual-learning control plane for Bees RL.

This module intentionally does not change PPO's on-policy update semantics. It owns the
persistent metadata and safety boundaries around training: immutable model identities,
candidate/champion promotion, historical-league regression pressure, rollback, and
validated archives for live telemetry and human demonstrations.

The first version uses only the Python standard library so it can run inside the same
ML-Agents virtual environment as Training/bees_mlagents_learn.py.
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import datetime as _dt
import hashlib
import json
import math
import os
import random
import shutil
import sqlite3
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence, Tuple


MODEL_STATUSES = {
    "training",
    "candidate",
    "rejected",
    "champion",
    "historical",
    "retired",
}

DEFAULT_CONFIG_PATH = Path(__file__).with_name("continual_learning_config.json")
DATABASE_NAME = "registry.sqlite3"
STATE_CHAMPION = "current_champion"
STATE_PREVIOUS_CHAMPION = "previous_champion"


class ContinualLearningError(RuntimeError):
    """Base error for invalid continual-learning operations."""


class CompatibilityError(ContinualLearningError):
    """Raised when model or experience schemas are incompatible."""


class PromotionError(ContinualLearningError):
    """Raised when promotion/rollback safety conditions are not met."""


class ValidationError(ContinualLearningError):
    """Raised for invalid or untrusted incoming data."""


@dataclass(frozen=True)
class Compatibility:
    behavior_name: str
    policy_abi_version: int
    observation_schema_version: int
    action_schema_version: int
    reward_schema_version: int
    scenario_schema_version: int

    @classmethod
    def from_config(cls, config: Mapping[str, Any]) -> "Compatibility":
        return cls(
            behavior_name=str(config["behavior_name"]),
            policy_abi_version=int(config["policy_abi_version"]),
            observation_schema_version=int(config["observation_schema_version"]),
            action_schema_version=int(config["action_schema_version"]),
            reward_schema_version=int(config["reward_schema_version"]),
            scenario_schema_version=int(config["scenario_schema_version"]),
        )

    def to_dict(self) -> Dict[str, Any]:
        return dataclasses.asdict(self)


@dataclass(frozen=True)
class PromotionDecision:
    passed: bool
    reasons: Tuple[str, ...]

    def to_dict(self) -> Dict[str, Any]:
        return {"passed": self.passed, "reasons": list(self.reasons)}


def utc_now() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).isoformat().replace("+00:00", "Z")


def load_config(path: Optional[os.PathLike[str] | str] = None) -> Dict[str, Any]:
    config_path = Path(path) if path is not None else DEFAULT_CONFIG_PATH
    try:
        raw = json.loads(config_path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ContinualLearningError(f"Continual-learning config not found: {config_path}") from exc
    except json.JSONDecodeError as exc:
        raise ContinualLearningError(f"Invalid JSON in continual-learning config {config_path}: {exc}") from exc

    required = {
        "behavior_name",
        "policy_abi_version",
        "observation_schema_version",
        "action_schema_version",
        "reward_schema_version",
        "scenario_schema_version",
        "promotion",
        "historical_league",
        "ingestion",
    }
    missing = sorted(required.difference(raw))
    if missing:
        raise ContinualLearningError(
            "Continual-learning config is missing required fields: " + ", ".join(missing)
        )
    return raw


def canonical_json(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(chunk_size)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def config_hash(path: Optional[os.PathLike[str] | str]) -> Optional[str]:
    if not path:
        return None
    source = Path(path)
    if not source.is_file():
        raise ContinualLearningError(f"Training config does not exist: {source}")
    return sha256_file(source)


def _finite_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(float(value))


def _walk_finite_numbers(value: Any, *, path: str = "$") -> None:
    if isinstance(value, bool) or value is None or isinstance(value, str):
        return
    if isinstance(value, (int, float)):
        if not math.isfinite(float(value)):
            raise ValidationError(f"Non-finite numeric value at {path}.")
        return
    if isinstance(value, list):
        for index, child in enumerate(value):
            _walk_finite_numbers(child, path=f"{path}[{index}]")
        return
    if isinstance(value, dict):
        for key, child in value.items():
            _walk_finite_numbers(child, path=f"{path}.{key}")
        return
    raise ValidationError(f"Unsupported value type at {path}: {type(value).__name__}.")


class ContinualLearningStore:
    """SQLite-backed control plane and immutable file registry."""

    def __init__(
        self,
        root: os.PathLike[str] | str,
        config: Optional[Mapping[str, Any]] = None,
    ) -> None:
        self.root = Path(root).expanduser().resolve()
        self.config = dict(config) if config is not None else load_config()
        self.compatibility = Compatibility.from_config(self.config)
        self.db_path = self.root / DATABASE_NAME

    @property
    def models_dir(self) -> Path:
        return self.root / "models"

    @property
    def experience_dir(self) -> Path:
        return self.root / "experience"

    @property
    def evaluation_dir(self) -> Path:
        return self.root / "evaluation"

    def initialize(self) -> None:
        for path in (
            self.root,
            self.models_dir / "candidates",
            self.models_dir / "champions",
            self.models_dir / "historical",
            self.models_dir / "rejected",
            self.models_dir / "retired",
            self.experience_dir / "raw-live",
            self.experience_dir / "human-demos",
            self.evaluation_dir / "reports",
        ):
            path.mkdir(parents=True, exist_ok=True)

        with self._connect() as db:
            db.executescript(
                """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS models (
                    model_id TEXT PRIMARY KEY,
                    artifact_sha256 TEXT NOT NULL UNIQUE,
                    artifact_name TEXT NOT NULL,
                    artifact_path TEXT NOT NULL,
                    parent_model_id TEXT NULL,
                    training_run_id TEXT NOT NULL,
                    training_step INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    game_build_version TEXT NOT NULL,
                    behavior_name TEXT NOT NULL,
                    policy_abi_version INTEGER NOT NULL,
                    observation_schema_version INTEGER NOT NULL,
                    action_schema_version INTEGER NOT NULL,
                    reward_schema_version INTEGER NOT NULL,
                    scenario_schema_version INTEGER NOT NULL,
                    training_config_hash TEXT NULL,
                    source_checkpoint TEXT NULL,
                    status TEXT NOT NULL,
                    evaluation_report_id TEXT NULL,
                    metadata_json TEXT NOT NULL,
                    FOREIGN KEY(parent_model_id) REFERENCES models(model_id)
                );

                CREATE TABLE IF NOT EXISTS state (
                    key TEXT PRIMARY KEY,
                    value TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS evaluations (
                    report_id TEXT PRIMARY KEY,
                    candidate_model_id TEXT NOT NULL,
                    champion_model_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    passed INTEGER NOT NULL,
                    reasons_json TEXT NOT NULL,
                    report_json TEXT NOT NULL,
                    FOREIGN KEY(candidate_model_id) REFERENCES models(model_id),
                    FOREIGN KEY(champion_model_id) REFERENCES models(model_id)
                );

                CREATE TABLE IF NOT EXISTS historical_matchups (
                    current_model_id TEXT NOT NULL,
                    opponent_model_id TEXT NOT NULL,
                    current_win_rate REAL NOT NULL,
                    previous_win_rate REAL NULL,
                    match_count INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    tags_json TEXT NOT NULL,
                    PRIMARY KEY(current_model_id, opponent_model_id),
                    FOREIGN KEY(current_model_id) REFERENCES models(model_id),
                    FOREIGN KEY(opponent_model_id) REFERENCES models(model_id)
                );

                CREATE TABLE IF NOT EXISTS telemetry_batches (
                    batch_id TEXT PRIMARY KEY,
                    match_id TEXT NOT NULL UNIQUE,
                    model_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL UNIQUE,
                    archive_path TEXT NOT NULL,
                    trusted_for_on_policy_rl INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(model_id) REFERENCES models(model_id)
                );

                CREATE TABLE IF NOT EXISTS demonstration_batches (
                    batch_id TEXT PRIMARY KEY,
                    demonstration_id TEXT NOT NULL UNIQUE,
                    model_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL UNIQUE,
                    archive_path TEXT NOT NULL,
                    example_count INTEGER NOT NULL,
                    FOREIGN KEY(model_id) REFERENCES models(model_id)
                );

                CREATE TABLE IF NOT EXISTS scenario_pressure (
                    scenario_id TEXT PRIMARY KEY,
                    source TEXT NOT NULL,
                    priority REAL NOT NULL,
                    descriptor_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_models_status ON models(status);
                CREATE INDEX IF NOT EXISTS idx_models_run_step ON models(training_run_id, training_step);
                CREATE INDEX IF NOT EXISTS idx_matchups_current ON historical_matchups(current_model_id);
                """
            )

    @contextlib.contextmanager
    def _connect(self):
        self.root.mkdir(parents=True, exist_ok=True)
        db = sqlite3.connect(str(self.db_path), timeout=30.0)
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys=ON")
        try:
            yield db
            db.commit()
        except Exception:
            db.rollback()
            raise
        finally:
            db.close()

    def _require_initialized(self) -> None:
        if not self.db_path.exists():
            raise ContinualLearningError(
                f"Store is not initialized at {self.root}. Run the init command first."
            )

    def _state(self, db: sqlite3.Connection, key: str) -> Optional[str]:
        row = db.execute("SELECT value FROM state WHERE key = ?", (key,)).fetchone()
        return None if row is None else row["value"]

    def _set_state(self, db: sqlite3.Connection, key: str, value: Optional[str]) -> None:
        db.execute(
            """
            INSERT INTO state(key, value) VALUES(?, ?)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """,
            (key, value),
        )

    def _model_row(self, db: sqlite3.Connection, model_id: str) -> sqlite3.Row:
        row = db.execute("SELECT * FROM models WHERE model_id = ?", (model_id,)).fetchone()
        if row is None:
            raise ContinualLearningError(f"Unknown model_id: {model_id}")
        return row

    @staticmethod
    def _row_dict(row: sqlite3.Row) -> Dict[str, Any]:
        value = dict(row)
        try:
            value["metadata"] = json.loads(value.pop("metadata_json"))
        except (KeyError, json.JSONDecodeError):
            pass
        return value

    def get_model(self, model_id: str) -> Dict[str, Any]:
        self._require_initialized()
        with self._connect() as db:
            return self._row_dict(self._model_row(db, model_id))

    def list_models(self, status: Optional[str] = None) -> List[Dict[str, Any]]:
        self._require_initialized()
        if status is not None and status not in MODEL_STATUSES:
            raise ValidationError(f"Unknown model status {status!r}.")
        with self._connect() as db:
            if status is None:
                rows = db.execute(
                    "SELECT * FROM models ORDER BY created_at DESC, training_step DESC"
                ).fetchall()
            else:
                rows = db.execute(
                    "SELECT * FROM models WHERE status = ? ORDER BY created_at DESC, training_step DESC",
                    (status,),
                ).fetchall()
            return [self._row_dict(row) for row in rows]

    def current_champion_id(self) -> Optional[str]:
        self._require_initialized()
        with self._connect() as db:
            return self._state(db, STATE_CHAMPION)

    def current_champion(self) -> Optional[Dict[str, Any]]:
        model_id = self.current_champion_id()
        return None if model_id is None else self.get_model(model_id)

    def register_model(
        self,
        artifact: os.PathLike[str] | str,
        *,
        training_run_id: str,
        training_step: int,
        game_build_version: str,
        parent_model_id: Optional[str] = None,
        training_config_path: Optional[os.PathLike[str] | str] = None,
        source_checkpoint: Optional[str] = None,
        status: str = "candidate",
        metadata: Optional[Mapping[str, Any]] = None,
    ) -> Dict[str, Any]:
        self._require_initialized()
        source = Path(artifact).expanduser().resolve()
        if not source.is_file():
            raise ValidationError(f"Model artifact not found: {source}")
        if source.stat().st_size <= 0:
            raise ValidationError(f"Model artifact is empty: {source}")
        if not training_run_id.strip():
            raise ValidationError("training_run_id is required.")
        if int(training_step) < 0:
            raise ValidationError("training_step must be >= 0.")
        if not game_build_version.strip():
            raise ValidationError("game_build_version is required.")
        if status not in MODEL_STATUSES:
            raise ValidationError(f"Unknown model status {status!r}.")
        if status == "champion":
            raise PromotionError("Register models as candidate/training; use promote() to make a champion.")

        artifact_hash = sha256_file(source)
        model_id = f"bees-rl-v{self.compatibility.policy_abi_version}-{artifact_hash[:24]}"
        cfg_hash = config_hash(training_config_path)
        created_at = utc_now()
        destination_dir = self.models_dir / self._directory_for_status(status)
        destination = destination_dir / f"{model_id}{source.suffix.lower() or '.model'}"
        metadata_dict = dict(metadata or {})
        metadata_dict.setdefault("registered_from", str(source))
        metadata_dict.setdefault("artifact_size", source.stat().st_size)

        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            destination_preexisted = destination.exists()
            existing = db.execute(
                "SELECT * FROM models WHERE artifact_sha256 = ?", (artifact_hash,)
            ).fetchone()
            if existing is not None:
                self._assert_registration_compatible(
                    existing,
                    training_run_id=training_run_id,
                    training_step=int(training_step),
                    game_build_version=game_build_version,
                    parent_model_id=parent_model_id,
                    training_config_hash=cfg_hash,
                    source_checkpoint=source_checkpoint,
                )
                return self._row_dict(existing)

            if parent_model_id is not None:
                parent = self._model_row(db, parent_model_id)
                self._assert_model_compatible(parent)

            self._copy_immutable(source, destination, artifact_hash)
            try:
                db.execute(
                    """
                    INSERT INTO models(
                        model_id, artifact_sha256, artifact_name, artifact_path,
                        parent_model_id, training_run_id, training_step, created_at,
                        game_build_version, behavior_name, policy_abi_version,
                        observation_schema_version, action_schema_version,
                        reward_schema_version, scenario_schema_version,
                        training_config_hash, source_checkpoint, status,
                        evaluation_report_id, metadata_json
                    ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                    """,
                    (
                        model_id,
                        artifact_hash,
                        source.name,
                        str(destination),
                        parent_model_id,
                        training_run_id,
                        int(training_step),
                        created_at,
                        game_build_version,
                        self.compatibility.behavior_name,
                        self.compatibility.policy_abi_version,
                        self.compatibility.observation_schema_version,
                        self.compatibility.action_schema_version,
                        self.compatibility.reward_schema_version,
                        self.compatibility.scenario_schema_version,
                        cfg_hash,
                        source_checkpoint,
                        status,
                        None,
                        canonical_json(metadata_dict),
                    ),
                )
            except Exception:
                if not destination_preexisted:
                    try:
                        destination.unlink()
                    except FileNotFoundError:
                        pass
                raise
            return self._row_dict(self._model_row(db, model_id))

    def _assert_registration_compatible(
        self,
        existing: sqlite3.Row,
        *,
        training_run_id: str,
        training_step: int,
        game_build_version: str,
        parent_model_id: Optional[str],
        training_config_hash: Optional[str],
        source_checkpoint: Optional[str],
    ) -> None:
        self._assert_model_compatible(existing)
        mismatches = []
        for key, expected in (
            ("training_run_id", training_run_id),
            ("training_step", training_step),
            ("game_build_version", game_build_version),
            ("parent_model_id", parent_model_id),
            ("training_config_hash", training_config_hash),
            ("source_checkpoint", source_checkpoint),
        ):
            if existing[key] != expected:
                mismatches.append(f"{key}={existing[key]!r} (requested {expected!r})")
        if mismatches:
            raise ValidationError(
                "The same model bytes are already registered with conflicting lineage metadata: "
                + "; ".join(mismatches)
            )

    def _assert_model_compatible(self, row: sqlite3.Row) -> None:
        expected = self.compatibility.to_dict()
        actual = {key: row[key] for key in expected}
        if actual != expected:
            raise CompatibilityError(
                f"Model {row['model_id']} is incompatible with this store: "
                f"expected {expected}, got {actual}."
            )

    @staticmethod
    def _copy_immutable(source: Path, destination: Path, expected_sha256: str) -> None:
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists():
            if sha256_file(destination) != expected_sha256:
                raise ContinualLearningError(
                    f"Refusing to overwrite conflicting immutable model artifact: {destination}"
                )
            return
        fd, temp_name = tempfile.mkstemp(
            prefix=destination.name + ".",
            suffix=".tmp",
            dir=str(destination.parent),
        )
        os.close(fd)
        temp = Path(temp_name)
        try:
            shutil.copy2(source, temp)
            if sha256_file(temp) != expected_sha256:
                raise ContinualLearningError(
                    f"Model artifact changed while being registered: {source}"
                )
            os.replace(temp, destination)
        finally:
            if temp.exists():
                temp.unlink()

    @staticmethod
    def _directory_for_status(status: str) -> str:
        if status == "candidate" or status == "training":
            return "candidates"
        if status == "champion":
            return "champions"
        return status

    @staticmethod
    def _assert_artifact_hash(path: Path, expected_sha256: str) -> None:
        if not path.is_file():
            raise ContinualLearningError(f"Registered model artifact is missing: {path}")
        if sha256_file(path) != expected_sha256:
            raise ContinualLearningError(
                f"Registered model artifact failed SHA-256 integrity verification: {path}"
            )

    def _stage_artifact_for_status(self, row: sqlite3.Row, status: str) -> str:
        current = Path(row["artifact_path"])
        expected_sha256 = row["artifact_sha256"]
        target_dir = self.models_dir / self._directory_for_status(status)
        target_dir.mkdir(parents=True, exist_ok=True)
        target = target_dir / current.name
        self._assert_artifact_hash(current, expected_sha256)
        if current.resolve() == target.resolve():
            return str(current)
        self._copy_immutable(current, target, expected_sha256)
        return str(target)

    def _cleanup_staged_source(
        self,
        source: Path,
        target: Path,
        expected_sha256: str,
    ) -> None:
        if source.resolve() == target.resolve() or not source.exists():
            return
        self._assert_artifact_hash(target, expected_sha256)
        try:
            self._assert_artifact_hash(source, expected_sha256)
            source.unlink()
        except (ContinualLearningError, OSError) as exc:
            print(
                f"warning: could not remove obsolete model artifact {source}: {exc}",
                file=sys.stderr,
            )

    def _promotion_policy_snapshot(self) -> Dict[str, Any]:
        return {
            "schema_version": 1,
            "compatibility": self.compatibility.to_dict(),
            "promotion": self.config["promotion"],
        }

    @staticmethod
    def _promotion_policy_fingerprint(policy: Mapping[str, Any]) -> str:
        return sha256_bytes(canonical_json(policy).encode("utf-8"))

    def assess_evaluation(self, report: Mapping[str, Any]) -> PromotionDecision:
        promotion = self.config["promotion"]
        reasons: List[str] = []

        candidate_id = str(report.get("candidate_model_id", ""))
        if not candidate_id:
            reasons.append("candidate_model_id is required")

        champion_id = self.current_champion_id()
        report_champion_id = report.get("champion_model_id")
        if champion_id is not None and report_champion_id != champion_id:
            reasons.append(
                f"evaluation champion mismatch: current={champion_id} report={report_champion_id}"
            )
        if champion_id is None and report_champion_id not in (None, ""):
            reasons.append("first champion evaluation must not claim an existing champion")

        comparison = report.get("candidate_vs_champion")
        if champion_id is not None:
            if not isinstance(comparison, dict):
                reasons.append("candidate_vs_champion result is required")
            else:
                wins = self._nonnegative_int(comparison.get("wins"), "candidate wins", reasons)
                losses = self._nonnegative_int(comparison.get("losses"), "candidate losses", reasons)
                draws = self._nonnegative_int(comparison.get("draws", 0), "candidate draws", reasons)
                if wins is not None and losses is not None and draws is not None:
                    matches = wins + losses + draws
                    if matches < int(promotion["min_matches_vs_champion"]):
                        reasons.append(
                            f"only {matches} champion matches; "
                            f"minimum is {promotion['min_matches_vs_champion']}"
                        )
                    if matches > 0:
                        win_rate = (wins + draws * 0.5) / matches
                        if win_rate < float(promotion["min_win_rate_vs_champion"]):
                            reasons.append(
                                f"champion win rate {win_rate:.4f} below "
                                f"{float(promotion['min_win_rate_vs_champion']):.4f}"
                            )

        historical = report.get("historical", [])
        if not isinstance(historical, list):
            reasons.append("historical must be a list")
            historical = []
        expected_compatibility = self.compatibility.to_dict()
        expected_historical_ids = {
            str(model["model_id"])
            for model in self.list_models(status="historical")
            if all(model.get(key) == value for key, value in expected_compatibility.items())
        }
        reported_historical_ids = set()
        critical_regressions = 0
        max_regression = float(promotion["max_historical_regression"])
        min_historical_matches = int(promotion["min_historical_matches_per_opponent"])
        for index, item in enumerate(historical):
            if not isinstance(item, dict):
                reasons.append(f"historical[{index}] must be an object")
                continue
            opponent_model_id = item.get("opponent_model_id")
            if not isinstance(opponent_model_id, str) or not opponent_model_id.strip():
                reasons.append(f"historical[{index}].opponent_model_id is required")
            else:
                opponent_model_id = opponent_model_id.strip()
                if opponent_model_id in reported_historical_ids:
                    reasons.append(
                        f"historical opponent {opponent_model_id} is duplicated"
                    )
                reported_historical_ids.add(opponent_model_id)
            matches = item.get("matches", 0)
            if not isinstance(matches, int) or isinstance(matches, bool) or matches < 0:
                reasons.append(f"historical[{index}].matches must be a non-negative integer")
                continue
            if matches < min_historical_matches:
                reasons.append(
                    f"historical[{index}] has only {matches} matches; "
                    f"minimum is {min_historical_matches}"
                )
            current = item.get("candidate_win_rate")
            baseline = item.get("baseline_win_rate")
            if not _finite_number(current) or not 0 <= float(current) <= 1:
                reasons.append(f"historical[{index}].candidate_win_rate must be in [0,1]")
                continue
            if champion_id is not None and baseline is None:
                reasons.append(f"historical[{index}].baseline_win_rate is required")
                continue
            if baseline is not None:
                if not _finite_number(baseline) or not 0 <= float(baseline) <= 1:
                    reasons.append(f"historical[{index}].baseline_win_rate must be in [0,1]")
                    continue
                regression = float(baseline) - float(current)
                if regression > max_regression:
                    if bool(item.get("critical", False)):
                        critical_regressions += 1
                    reasons.append(
                        f"historical[{index}] regressed {regression:.4f}; "
                        f"maximum is {max_regression:.4f}"
                    )

        missing_historical_ids = sorted(expected_historical_ids - reported_historical_ids)
        unexpected_historical_ids = sorted(reported_historical_ids - expected_historical_ids)
        if missing_historical_ids:
            reasons.append(
                "missing historical opponents: " + ", ".join(missing_historical_ids)
            )
        if unexpected_historical_ids:
            reasons.append(
                "unexpected historical opponents: " + ", ".join(unexpected_historical_ids)
            )

        competencies = report.get("competencies", [])
        if not isinstance(competencies, list):
            reasons.append("competencies must be a list")
            competencies = []
        minimum_competencies = int(promotion.get("min_competency_cases", 1))
        if len(competencies) < minimum_competencies:
            reasons.append(
                f"only {len(competencies)} competency cases; minimum is {minimum_competencies}"
            )
        for index, item in enumerate(competencies):
            if not isinstance(item, dict):
                reasons.append(f"competencies[{index}] must be an object")
                continue
            score = item.get("score")
            minimum = item.get("minimum")
            if not _finite_number(score) or not _finite_number(minimum):
                reasons.append(f"competencies[{index}] score/minimum must be finite numbers")
                continue
            if float(score) < float(minimum):
                if bool(item.get("critical", False)):
                    critical_regressions += 1
                reasons.append(
                    f"competency {item.get('name', index)!r} score {float(score):.4f} "
                    f"below {float(minimum):.4f}"
                )

        if critical_regressions > int(promotion["max_critical_regressions"]):
            reasons.append(
                f"{critical_regressions} critical regressions exceeds "
                f"{promotion['max_critical_regressions']}"
            )

        for field in ("behavior_sanity_passed", "runtime_compatible", "runtime_checks_passed"):
            if report.get(field) is not True:
                reasons.append(f"{field} must be true")

        return PromotionDecision(passed=not reasons, reasons=tuple(reasons))

    @staticmethod
    def _nonnegative_int(value: Any, label: str, reasons: List[str]) -> Optional[int]:
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            reasons.append(f"{label} must be a non-negative integer")
            return None
        return value

    def record_evaluation(self, report: Mapping[str, Any]) -> Dict[str, Any]:
        self._require_initialized()
        _walk_finite_numbers(report)
        candidate_id = str(report.get("candidate_model_id", ""))
        if not candidate_id:
            raise ValidationError("Evaluation candidate_model_id is required.")

        report_champion_value = report.get("champion_model_id")
        if report_champion_value in (None, ""):
            report_champion_id = None
        elif not isinstance(report_champion_value, str) or not report_champion_value.strip():
            raise ValidationError(
                "Evaluation champion_model_id must be a non-empty string when provided."
            )
        else:
            report_champion_id = report_champion_value.strip()

        with self._connect() as db:
            candidate = self._model_row(db, candidate_id)
            self._assert_model_compatible(candidate)
            if report_champion_id is not None:
                report_champion = self._model_row(db, report_champion_id)
                self._assert_model_compatible(report_champion)

        decision = self.assess_evaluation(report)
        body = dict(report)
        promotion_policy = self._promotion_policy_snapshot()
        body["promotion_policy"] = promotion_policy
        body["promotion_policy_fingerprint"] = self._promotion_policy_fingerprint(
            promotion_policy
        )
        body["decision"] = decision.to_dict()
        report_hash = sha256_bytes(canonical_json(body).encode("utf-8"))
        report_id = f"eval-{report_hash[:24]}"
        created_at = utc_now()
        report_path = self.evaluation_dir / "reports" / f"{report_id}.json"
        self._write_json_immutable(report_path, body)

        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute(
                "SELECT * FROM evaluations WHERE report_id = ?", (report_id,)
            ).fetchone()
            if existing is None:
                db.execute(
                    """
                    INSERT INTO evaluations(
                        report_id, candidate_model_id, champion_model_id, created_at,
                        passed, reasons_json, report_json
                    ) VALUES(?,?,?,?,?,?,?)
                    """,
                    (
                        report_id,
                        candidate_id,
                        report_champion_id,
                        created_at,
                        int(decision.passed),
                        canonical_json(list(decision.reasons)),
                        canonical_json(body),
                    ),
                )
            return {
                "report_id": report_id,
                "candidate_model_id": candidate_id,
                "passed": decision.passed,
                "reasons": list(decision.reasons),
                "report_path": str(report_path),
            }

    @staticmethod
    def _write_json_immutable(path: Path, value: Mapping[str, Any]) -> None:
        payload = (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists():
            if path.read_bytes() != payload:
                raise ContinualLearningError(f"Refusing to overwrite immutable record: {path}")
            return
        fd, temp_name = tempfile.mkstemp(
            prefix=path.name + ".", suffix=".tmp", dir=str(path.parent)
        )
        try:
            with os.fdopen(fd, "wb") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp_name, path)
        finally:
            try:
                os.unlink(temp_name)
            except FileNotFoundError:
                pass

    def promote(self, candidate_model_id: str, evaluation_report_id: str) -> Dict[str, Any]:
        self._require_initialized()
        cleanup: List[Tuple[Path, Path, str]] = []
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            candidate = self._model_row(db, candidate_model_id)
            self._assert_model_compatible(candidate)
            if candidate["status"] not in ("candidate", "rejected"):
                raise PromotionError(
                    f"Model {candidate_model_id} has status {candidate['status']}; "
                    "only candidate/rejected models may be promoted."
                )
            evaluation = db.execute(
                "SELECT * FROM evaluations WHERE report_id = ?", (evaluation_report_id,)
            ).fetchone()
            if evaluation is None:
                raise PromotionError(f"Unknown evaluation report: {evaluation_report_id}")
            if evaluation["candidate_model_id"] != candidate_model_id:
                raise PromotionError("Evaluation report belongs to a different candidate.")
            if not bool(evaluation["passed"]):
                raise PromotionError(
                    "Candidate failed its evaluation: "
                    + "; ".join(json.loads(evaluation["reasons_json"]))
                )

            try:
                report_body = json.loads(evaluation["report_json"])
            except (TypeError, json.JSONDecodeError) as exc:
                raise PromotionError("Evaluation report payload is corrupted.") from exc
            if not isinstance(report_body, dict):
                raise PromotionError("Evaluation report payload must be an object.")
            if report_body.get("candidate_model_id") != candidate_model_id:
                raise PromotionError(
                    "Evaluation record candidate does not match the candidate in its report."
                )
            report_champion_id = report_body.get("champion_model_id")
            if report_champion_id == "":
                report_champion_id = None
            if report_champion_id != evaluation["champion_model_id"]:
                raise PromotionError(
                    "Evaluation record champion does not match the champion in its report."
                )

            report_policy = report_body.get("promotion_policy")
            report_policy_fingerprint = report_body.get("promotion_policy_fingerprint")
            if (
                not isinstance(report_policy, dict)
                or not isinstance(report_policy_fingerprint, str)
                or not report_policy_fingerprint
            ):
                raise PromotionError(
                    "Evaluation report is not bound to a promotion policy; re-evaluate candidate "
                    "under the current promotion policy."
                )
            if self._promotion_policy_fingerprint(report_policy) != report_policy_fingerprint:
                raise PromotionError("Evaluation report promotion-policy fingerprint is corrupted.")
            current_policy = self._promotion_policy_snapshot()
            if self._promotion_policy_fingerprint(current_policy) != report_policy_fingerprint:
                raise PromotionError(
                    "Promotion policy changed after this evaluation; re-evaluate candidate under "
                    "the current promotion policy."
                )

            current = self._state(db, STATE_CHAMPION)
            if current is None:
                raise PromotionError(
                    "No current champion is established; use the explicit generation-zero "
                    "bootstrap command before normal promotion."
                )
            if report_champion_id != current:
                raise PromotionError(
                    "Champion changed after this evaluation; re-evaluate candidate against current champion."
                )

            report_historical = report_body.get("historical")
            if not isinstance(report_historical, list):
                raise PromotionError("Evaluation report historical results are corrupted.")
            reported_historical_ids: List[str] = []
            for index, item in enumerate(report_historical):
                if not isinstance(item, dict):
                    raise PromotionError(
                        f"Evaluation report historical[{index}] is corrupted."
                    )
                opponent_id = item.get("opponent_model_id")
                if not isinstance(opponent_id, str) or not opponent_id.strip():
                    raise PromotionError(
                        f"Evaluation report historical[{index}] is missing opponent_model_id."
                    )
                reported_historical_ids.append(opponent_id.strip())
            if len(reported_historical_ids) != len(set(reported_historical_ids)):
                raise PromotionError("Evaluation report contains duplicate historical opponents.")

            compatibility = self.compatibility
            historical_rows = db.execute(
                """
                SELECT model_id
                FROM models
                WHERE status = 'historical'
                  AND behavior_name = ?
                  AND policy_abi_version = ?
                  AND observation_schema_version = ?
                  AND action_schema_version = ?
                  AND reward_schema_version = ?
                  AND scenario_schema_version = ?
                """,
                (
                    compatibility.behavior_name,
                    compatibility.policy_abi_version,
                    compatibility.observation_schema_version,
                    compatibility.action_schema_version,
                    compatibility.reward_schema_version,
                    compatibility.scenario_schema_version,
                ),
            ).fetchall()
            current_historical_ids = {str(row["model_id"]) for row in historical_rows}
            if set(reported_historical_ids) != current_historical_ids:
                raise PromotionError(
                    "Historical league changed after this evaluation; re-evaluate candidate "
                    "against the current historical league."
                )

            old = self._model_row(db, current)
            old_source = Path(old["artifact_path"])
            old_path = self._stage_artifact_for_status(old, "historical")
            cleanup.append((old_source, Path(old_path), old["artifact_sha256"]))
            db.execute(
                "UPDATE models SET status = 'historical', artifact_path = ? WHERE model_id = ?",
                (old_path, current),
            )
            self._set_state(db, STATE_PREVIOUS_CHAMPION, current)

            candidate_source = Path(candidate["artifact_path"])
            new_path = self._stage_artifact_for_status(candidate, "champion")
            cleanup.append((candidate_source, Path(new_path), candidate["artifact_sha256"]))
            db.execute(
                """
                UPDATE models
                SET status = 'champion', artifact_path = ?, evaluation_report_id = ?
                WHERE model_id = ?
                """,
                (new_path, evaluation_report_id, candidate_model_id),
            )
            self._set_state(db, STATE_CHAMPION, candidate_model_id)
            result = self._row_dict(self._model_row(db, candidate_model_id))

        for source, target, expected_sha256 in cleanup:
            self._cleanup_staged_source(source, target, expected_sha256)
        return result

    def reject(self, candidate_model_id: str, evaluation_report_id: str) -> Dict[str, Any]:
        self._require_initialized()
        cleanup: List[Tuple[Path, Path, str]] = []
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            candidate = self._model_row(db, candidate_model_id)
            if candidate["status"] != "candidate":
                raise PromotionError("Only candidate models may be rejected.")
            evaluation = db.execute(
                "SELECT * FROM evaluations WHERE report_id = ?", (evaluation_report_id,)
            ).fetchone()
            if evaluation is None or evaluation["candidate_model_id"] != candidate_model_id:
                raise PromotionError("A matching evaluation report is required.")
            candidate_source = Path(candidate["artifact_path"])
            target = self._stage_artifact_for_status(candidate, "rejected")
            cleanup.append((candidate_source, Path(target), candidate["artifact_sha256"]))
            db.execute(
                """
                UPDATE models
                SET status='rejected', artifact_path=?, evaluation_report_id=?
                WHERE model_id=?
                """,
                (target, evaluation_report_id, candidate_model_id),
            )
            result = self._row_dict(self._model_row(db, candidate_model_id))

        for source, target, expected_sha256 in cleanup:
            self._cleanup_staged_source(source, target, expected_sha256)
        return result

    def rollback(self, target_model_id: Optional[str] = None) -> Dict[str, Any]:
        self._require_initialized()
        cleanup: List[Tuple[Path, Path, str]] = []
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            current_id = self._state(db, STATE_CHAMPION)
            if current_id is None:
                raise PromotionError("Cannot roll back because there is no current champion.")
            if target_model_id is None:
                target_model_id = self._state(db, STATE_PREVIOUS_CHAMPION)
            if not target_model_id:
                raise PromotionError("No previous champion is recorded for rollback.")
            if target_model_id == current_id:
                raise PromotionError("Rollback target is already the current champion.")

            current = self._model_row(db, current_id)
            target = self._model_row(db, target_model_id)
            self._assert_model_compatible(target)
            if target["status"] not in ("historical", "champion"):
                raise PromotionError(
                    f"Rollback target {target_model_id} has status {target['status']}."
                )

            current_source = Path(current["artifact_path"])
            target_source = Path(target["artifact_path"])
            old_path = self._stage_artifact_for_status(current, "historical")
            target_path = self._stage_artifact_for_status(target, "champion")
            cleanup.append((current_source, Path(old_path), current["artifact_sha256"]))
            cleanup.append((target_source, Path(target_path), target["artifact_sha256"]))
            db.execute(
                "UPDATE models SET status='historical', artifact_path=? WHERE model_id=?",
                (old_path, current_id),
            )
            db.execute(
                "UPDATE models SET status='champion', artifact_path=? WHERE model_id=?",
                (target_path, target_model_id),
            )
            self._set_state(db, STATE_CHAMPION, target_model_id)
            self._set_state(db, STATE_PREVIOUS_CHAMPION, current_id)
            result = self._row_dict(self._model_row(db, target_model_id))

        for source, target, expected_sha256 in cleanup:
            self._cleanup_staged_source(source, target, expected_sha256)
        return result

    def record_historical_matchup(
        self,
        *,
        current_model_id: str,
        opponent_model_id: str,
        current_win_rate: float,
        match_count: int,
        previous_win_rate: Optional[float] = None,
        tags: Optional[Sequence[str]] = None,
    ) -> None:
        self._require_initialized()
        for label, value in (
            ("current_win_rate", current_win_rate),
            ("previous_win_rate", previous_win_rate),
        ):
            if value is not None and (not _finite_number(value) or not 0 <= float(value) <= 1):
                raise ValidationError(f"{label} must be in [0,1].")
        if not isinstance(match_count, int) or isinstance(match_count, bool) or match_count <= 0:
            raise ValidationError("match_count must be a positive integer.")

        with self._connect() as db:
            current = self._model_row(db, current_model_id)
            opponent = self._model_row(db, opponent_model_id)
            self._assert_model_compatible(current)
            self._assert_model_compatible(opponent)
            if opponent["status"] not in ("historical", "champion"):
                raise ValidationError(
                    f"Opponent {opponent_model_id} is not a historical/champion policy."
                )
            db.execute(
                """
                INSERT INTO historical_matchups(
                    current_model_id, opponent_model_id, current_win_rate,
                    previous_win_rate, match_count, updated_at, tags_json
                ) VALUES(?,?,?,?,?,?,?)
                ON CONFLICT(current_model_id, opponent_model_id) DO UPDATE SET
                    current_win_rate=excluded.current_win_rate,
                    previous_win_rate=excluded.previous_win_rate,
                    match_count=excluded.match_count,
                    updated_at=excluded.updated_at,
                    tags_json=excluded.tags_json
                """,
                (
                    current_model_id,
                    opponent_model_id,
                    float(current_win_rate),
                    None if previous_win_rate is None else float(previous_win_rate),
                    match_count,
                    utc_now(),
                    canonical_json(list(tags or [])),
                ),
            )

    def historical_sampling_weights(self, current_model_id: str) -> List[Dict[str, Any]]:
        self._require_initialized()
        settings = self.config["historical_league"]
        base = float(settings["base_weight"])
        trigger = float(settings["weakness_trigger_regression"])
        scale = float(settings["weakness_bonus_scale"])
        cap = float(settings["max_weight_multiplier"])

        with self._connect() as db:
            current = self._model_row(db, current_model_id)
            self._assert_model_compatible(current)
            rows = db.execute(
                """
                SELECT m.model_id, m.status,
                       h.current_win_rate, h.previous_win_rate,
                       h.match_count, h.tags_json
                FROM models m
                LEFT JOIN historical_matchups h
                  ON h.opponent_model_id = m.model_id
                 AND h.current_model_id = ?
                WHERE m.status IN ('historical', 'champion')
                  AND m.model_id <> ?
                  AND m.behavior_name = ?
                  AND m.policy_abi_version = ?
                  AND m.observation_schema_version = ?
                  AND m.action_schema_version = ?
                  AND m.reward_schema_version = ?
                  AND m.scenario_schema_version = ?
                ORDER BY m.created_at ASC, m.model_id ASC
                """,
                (
                    current_model_id,
                    current_model_id,
                    self.compatibility.behavior_name,
                    self.compatibility.policy_abi_version,
                    self.compatibility.observation_schema_version,
                    self.compatibility.action_schema_version,
                    self.compatibility.reward_schema_version,
                    self.compatibility.scenario_schema_version,
                ),
            ).fetchall()

        result = []
        for row in rows:
            regression = 0.0
            if row["previous_win_rate"] is not None and row["current_win_rate"] is not None:
                regression = max(
                    0.0,
                    float(row["previous_win_rate"]) - float(row["current_win_rate"]) - trigger,
                )
            multiplier = min(cap, 1.0 + regression * scale)
            result.append(
                {
                    "model_id": row["model_id"],
                    "status": row["status"],
                    "weight": base * multiplier,
                    "regression": regression,
                    "match_count": row["match_count"] or 0,
                    "tags": json.loads(row["tags_json"]) if row["tags_json"] else [],
                }
            )
        return result

    def sample_historical_opponent(
        self,
        current_model_id: str,
        *,
        rng: Optional[random.Random] = None,
    ) -> Dict[str, Any]:
        weighted = self.historical_sampling_weights(current_model_id)
        if not weighted:
            raise ContinualLearningError("No compatible historical opponents are available.")
        chooser = rng or random.SystemRandom()
        total = sum(max(0.0, float(item["weight"])) for item in weighted)
        if total <= 0:
            raise ContinualLearningError("Historical opponent weights sum to zero.")
        target = chooser.random() * total
        running = 0.0
        for item in weighted:
            running += max(0.0, float(item["weight"]))
            if target <= running:
                return item
        return weighted[-1]

    def upsert_scenario_pressure(
        self,
        scenario_id: str,
        *,
        source: str,
        priority: float,
        descriptor: Mapping[str, Any],
    ) -> None:
        self._require_initialized()
        if not scenario_id.strip() or not source.strip():
            raise ValidationError("scenario_id and source are required.")
        if not _finite_number(priority) or float(priority) < 0:
            raise ValidationError("scenario priority must be a finite non-negative number.")
        _walk_finite_numbers(descriptor)
        with self._connect() as db:
            db.execute(
                """
                INSERT INTO scenario_pressure(
                    scenario_id, source, priority, descriptor_json, updated_at
                ) VALUES(?,?,?,?,?)
                ON CONFLICT(scenario_id) DO UPDATE SET
                    source=excluded.source,
                    priority=excluded.priority,
                    descriptor_json=excluded.descriptor_json,
                    updated_at=excluded.updated_at
                """,
                (
                    scenario_id,
                    source,
                    float(priority),
                    canonical_json(dict(descriptor)),
                    utc_now(),
                ),
            )

    def ingest_telemetry(self, payload: Mapping[str, Any]) -> Dict[str, Any]:
        self._require_initialized()
        ingestion = self.config["ingestion"]
        encoded = canonical_json(payload).encode("utf-8")
        if len(encoded) > int(ingestion["max_payload_bytes"]):
            raise ValidationError("Telemetry payload exceeds configured maximum size.")
        _walk_finite_numbers(payload)

        match_id = self._required_identifier(payload, "match_id")
        model_id = self._required_identifier(payload, "model_id")
        self._validate_experience_compatibility(payload, model_id)
        mode = self._required_identifier(payload, "mode")
        self._required_identifier(payload, "game_build_version")
        self._required_identifier(payload, "result")

        steps = payload.get("steps", [])
        if not isinstance(steps, list):
            raise ValidationError("steps must be a list when provided.")
        if len(steps) > int(ingestion["max_steps_per_match"]):
            raise ValidationError("Telemetry step count exceeds configured maximum.")

        payload_hash = sha256_bytes(encoded)
        batch_id = f"telemetry-{payload_hash[:24]}"
        archive = self.experience_dir / "raw-live" / f"{batch_id}.json"

        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            duplicate = db.execute(
                """
                SELECT * FROM telemetry_batches
                WHERE match_id = ? OR payload_sha256 = ?
                """,
                (match_id, payload_hash),
            ).fetchone()
            if duplicate is not None:
                if duplicate["match_id"] == match_id and duplicate["payload_sha256"] != payload_hash:
                    raise ValidationError(
                        f"match_id {match_id!r} was already ingested with different content."
                    )
                return {
                    "batch_id": duplicate["batch_id"],
                    "match_id": duplicate["match_id"],
                    "duplicate": True,
                    "archive_path": duplicate["archive_path"],
                    "trusted_for_on_policy_rl": False,
                }

            self._write_json_immutable(archive, dict(payload))
            db.execute(
                """
                INSERT INTO telemetry_batches(
                    batch_id, match_id, model_id, created_at, payload_sha256,
                    archive_path, trusted_for_on_policy_rl
                ) VALUES(?,?,?,?,?,?,0)
                """,
                (
                    batch_id,
                    match_id,
                    model_id,
                    utc_now(),
                    payload_hash,
                    str(archive),
                ),
            )
        return {
            "batch_id": batch_id,
            "match_id": match_id,
            "mode": mode,
            "duplicate": False,
            "archive_path": str(archive),
            "trusted_for_on_policy_rl": False,
        }

    def ingest_demonstration(self, payload: Mapping[str, Any]) -> Dict[str, Any]:
        self._require_initialized()
        ingestion = self.config["ingestion"]
        encoded = canonical_json(payload).encode("utf-8")
        if len(encoded) > int(ingestion["max_payload_bytes"]):
            raise ValidationError("Demonstration payload exceeds configured maximum size.")
        _walk_finite_numbers(payload)

        demonstration_id = self._required_identifier(payload, "demonstration_id")
        model_id = self._required_identifier(payload, "model_id")
        self._required_identifier(payload, "game_build_version")
        self._validate_experience_compatibility(payload, model_id)

        examples = payload.get("examples")
        if not isinstance(examples, list) or not examples:
            raise ValidationError("Demonstration examples must be a non-empty list.")
        if len(examples) > int(ingestion["max_steps_per_match"]):
            raise ValidationError("Demonstration example count exceeds configured maximum.")
        for index, example in enumerate(examples):
            if not isinstance(example, dict):
                raise ValidationError(f"examples[{index}] must be an object.")
            if "observation" not in example or "action" not in example:
                raise ValidationError(
                    f"examples[{index}] must contain observation and action."
                )

        payload_hash = sha256_bytes(encoded)
        batch_id = f"demo-{payload_hash[:24]}"
        archive = self.experience_dir / "human-demos" / f"{batch_id}.json"

        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            duplicate = db.execute(
                """
                SELECT * FROM demonstration_batches
                WHERE demonstration_id = ? OR payload_sha256 = ?
                """,
                (demonstration_id, payload_hash),
            ).fetchone()
            if duplicate is not None:
                if (
                    duplicate["demonstration_id"] == demonstration_id
                    and duplicate["payload_sha256"] != payload_hash
                ):
                    raise ValidationError(
                        f"demonstration_id {demonstration_id!r} was already ingested with different content."
                    )
                return {
                    "batch_id": duplicate["batch_id"],
                    "demonstration_id": duplicate["demonstration_id"],
                    "duplicate": True,
                    "example_count": duplicate["example_count"],
                    "archive_path": duplicate["archive_path"],
                }

            self._write_json_immutable(archive, dict(payload))
            db.execute(
                """
                INSERT INTO demonstration_batches(
                    batch_id, demonstration_id, model_id, created_at,
                    payload_sha256, archive_path, example_count
                ) VALUES(?,?,?,?,?,?,?)
                """,
                (
                    batch_id,
                    demonstration_id,
                    model_id,
                    utc_now(),
                    payload_hash,
                    str(archive),
                    len(examples),
                ),
            )
        return {
            "batch_id": batch_id,
            "demonstration_id": demonstration_id,
            "duplicate": False,
            "example_count": len(examples),
            "archive_path": str(archive),
        }

    @staticmethod
    def _required_identifier(payload: Mapping[str, Any], key: str) -> str:
        value = payload.get(key)
        if not isinstance(value, str) or not value.strip() or len(value) > 256:
            raise ValidationError(f"{key} must be a non-empty string up to 256 characters.")
        return value.strip()

    def _validate_experience_compatibility(
        self,
        payload: Mapping[str, Any],
        model_id: str,
    ) -> None:
        with self._connect() as db:
            model = self._model_row(db, model_id)
            self._assert_model_compatible(model)
        expected = {
            "behavior_name": self.compatibility.behavior_name,
            "policy_abi_version": self.compatibility.policy_abi_version,
            "observation_schema_version": self.compatibility.observation_schema_version,
            "action_schema_version": self.compatibility.action_schema_version,
            "reward_schema_version": self.compatibility.reward_schema_version,
            "scenario_schema_version": self.compatibility.scenario_schema_version,
        }
        for key, expected_value in expected.items():
            actual = payload.get(key)
            if actual != expected_value:
                raise CompatibilityError(
                    f"Experience {key}={actual!r} is incompatible; expected {expected_value!r}."
                )

    def status(self) -> Dict[str, Any]:
        self._require_initialized()
        with self._connect() as db:
            champion = self._state(db, STATE_CHAMPION)
            previous = self._state(db, STATE_PREVIOUS_CHAMPION)
            counts = {
                row["status"]: row["count"]
                for row in db.execute(
                    "SELECT status, COUNT(*) AS count FROM models GROUP BY status"
                ).fetchall()
            }
            telemetry_count = db.execute(
                "SELECT COUNT(*) AS count FROM telemetry_batches"
            ).fetchone()["count"]
            demo_count = db.execute(
                "SELECT COUNT(*) AS count FROM demonstration_batches"
            ).fetchone()["count"]
            evaluation_count = db.execute(
                "SELECT COUNT(*) AS count FROM evaluations"
            ).fetchone()["count"]
            pressure_count = db.execute(
                "SELECT COUNT(*) AS count FROM scenario_pressure"
            ).fetchone()["count"]
        return {
            "root": str(self.root),
            "compatibility": self.compatibility.to_dict(),
            "current_champion": champion,
            "previous_champion": previous,
            "model_counts": counts,
            "telemetry_batches": telemetry_count,
            "demonstration_batches": demo_count,
            "evaluations": evaluation_count,
            "scenario_pressure_entries": pressure_count,
        }


def _read_json(path: os.PathLike[str] | str) -> Any:
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ContinualLearningError(f"JSON file not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Invalid JSON in {path}: {exc}") from exc


def _print_json(value: Any) -> None:
    print(json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False))


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Bees continual-learning registry, promotion gate, league, and ingestion tools."
    )
    parser.add_argument(
        "--root",
        required=True,
        help="Persistent continual-learning root directory.",
    )
    parser.add_argument(
        "--config",
        default=str(DEFAULT_CONFIG_PATH),
        help="Continual-learning configuration JSON.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("init", help="Initialize storage and schema.")
    sub.add_parser("status", help="Show current champion and store counts.")

    register = sub.add_parser("register-model", help="Register an immutable candidate model.")
    register.add_argument("artifact")
    register.add_argument("--run-id", required=True)
    register.add_argument("--step", type=int, required=True)
    register.add_argument("--game-build", required=True)
    register.add_argument("--parent-model-id")
    register.add_argument("--training-config")
    register.add_argument("--source-checkpoint")
    register.add_argument("--status", choices=("candidate", "training"), default="candidate")

    evaluate = sub.add_parser("record-evaluation", help="Validate/store an evaluation report.")
    evaluate.add_argument("report")

    promote = sub.add_parser("promote", help="Promote a candidate that has a passing report.")
    promote.add_argument("model_id")
    promote.add_argument("evaluation_report_id")

    reject = sub.add_parser("reject", help="Mark a candidate rejected with its evaluation report.")
    reject.add_argument("model_id")
    reject.add_argument("evaluation_report_id")

    rollback = sub.add_parser("rollback", help="Roll back to the previous or specified champion.")
    rollback.add_argument("--model-id")

    matchup = sub.add_parser(
        "record-matchup", help="Record current-vs-historical regression evidence."
    )
    matchup.add_argument("--current", required=True)
    matchup.add_argument("--opponent", required=True)
    matchup.add_argument("--win-rate", type=float, required=True)
    matchup.add_argument("--previous-win-rate", type=float)
    matchup.add_argument("--matches", type=int, required=True)
    matchup.add_argument("--tag", action="append", default=[])

    weights = sub.add_parser("league-weights", help="Show historical opponent sampling weights.")
    weights.add_argument("--current", required=True)

    sample = sub.add_parser("sample-opponent", help="Sample one weighted historical opponent.")
    sample.add_argument("--current", required=True)
    sample.add_argument("--seed", type=int)

    telemetry = sub.add_parser("ingest-telemetry", help="Validate/archive one live match payload.")
    telemetry.add_argument("payload")

    demo = sub.add_parser(
        "ingest-demonstration", help="Validate/archive one human demonstration payload."
    )
    demo.add_argument("payload")

    pressure = sub.add_parser("scenario-pressure", help="Add/update replay/training scenario pressure.")
    pressure.add_argument("scenario_id")
    pressure.add_argument("--source", required=True)
    pressure.add_argument("--priority", type=float, required=True)
    pressure.add_argument("--descriptor", required=True)

    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    config = load_config(args.config)
    store = ContinualLearningStore(args.root, config)

    try:
        if args.command == "init":
            store.initialize()
            _print_json(store.status())
            return 0

        store._require_initialized()

        if args.command == "status":
            _print_json(store.status())
        elif args.command == "register-model":
            _print_json(
                store.register_model(
                    args.artifact,
                    training_run_id=args.run_id,
                    training_step=args.step,
                    game_build_version=args.game_build,
                    parent_model_id=args.parent_model_id,
                    training_config_path=args.training_config,
                    source_checkpoint=args.source_checkpoint,
                    status=args.status,
                )
            )
        elif args.command == "record-evaluation":
            _print_json(store.record_evaluation(_read_json(args.report)))
        elif args.command == "promote":
            _print_json(store.promote(args.model_id, args.evaluation_report_id))
        elif args.command == "reject":
            _print_json(store.reject(args.model_id, args.evaluation_report_id))
        elif args.command == "rollback":
            _print_json(store.rollback(args.model_id))
        elif args.command == "record-matchup":
            store.record_historical_matchup(
                current_model_id=args.current,
                opponent_model_id=args.opponent,
                current_win_rate=args.win_rate,
                previous_win_rate=args.previous_win_rate,
                match_count=args.matches,
                tags=args.tag,
            )
            _print_json({"recorded": True})
        elif args.command == "league-weights":
            _print_json(store.historical_sampling_weights(args.current))
        elif args.command == "sample-opponent":
            rng = random.Random(args.seed) if args.seed is not None else None
            _print_json(store.sample_historical_opponent(args.current, rng=rng))
        elif args.command == "ingest-telemetry":
            _print_json(store.ingest_telemetry(_read_json(args.payload)))
        elif args.command == "ingest-demonstration":
            _print_json(store.ingest_demonstration(_read_json(args.payload)))
        elif args.command == "scenario-pressure":
            descriptor = _read_json(args.descriptor)
            if not isinstance(descriptor, dict):
                raise ValidationError("Scenario descriptor must be a JSON object.")
            store.upsert_scenario_pressure(
                args.scenario_id,
                source=args.source,
                priority=args.priority,
                descriptor=descriptor,
            )
            _print_json({"recorded": True, "scenario_id": args.scenario_id})
        else:
            parser.error(f"Unknown command {args.command!r}")
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())