"""Focused tests for immutable scripted adversarial replay compilation."""

from __future__ import annotations

import json
from pathlib import Path
import struct
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import bees_continual_adversarial_mine as mine
import bees_continual_adversarial_replay as replay
from bees_continual_learning import ContinualLearningError, ValidationError


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]
SCENARIO_ID = "adv-" + "a" * 24
OTHER_SCENARIO_ID = "adv-" + "b" * 24
BATCH_ID = "demo-" + "c" * 24


def normalize_positive(value: float, scale: float) -> float:
    return value / (value + scale)


def set_enum_bits(values, start, bits, value):
    for bit in range(bits):
        values[start + bit] = 1.0 if value & (1 << bit) else 0.0


def observation(*, self_ship=13, enemy_ship=21, enemy_visible=True):
    values = [0.0] * OBSERVATION_SIZE
    usable = 86.0
    set_enum_bits(values, replay.SELF_SHIP_BIT_START, replay.SHIP_TYPE_BIT_COUNT, self_ship)
    values[replay.SELF_POSITION_X_INDEX] = 0.0
    values[replay.SELF_POSITION_Y_INDEX] = 0.5
    values[replay.LEVEL_SIZE_X_INDEX] = normalize_positive(usable, 100.0)
    values[replay.LEVEL_SIZE_Y_INDEX] = normalize_positive(usable, 100.0)
    if enemy_visible:
        values[mine.FIRST_ENEMY_SLOT_INDEX] = 1.0
        set_enum_bits(values, mine.ENEMY_SHIP_BIT_START, replay.SHIP_TYPE_BIT_COUNT, enemy_ship)
    return values


class FakeStore:
    def __init__(self, root: Path):
        self.root = root
        self.experience_dir = root / "experience"
        self.compatibility = SimpleNamespace(policy_abi_version=7)

    def _require_initialized(self):
        return None


class AdversarialReplayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.store = FakeStore(self.root)
        self.demo = Path(self.temp.name) / "approved.demo"
        self.demo.write_bytes(b"approved-demo")
        self.capture_manifest = {
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }
        self.archive = {
            "demo": self.demo,
            "demo_sha256": "d" * 64,
            "row": {"payload_sha256": "p" * 64},
            "capture_manifest_metadata": self.capture_manifest,
        }
        self.scenario = self.make_scenario(SCENARIO_ID)
        self.other_scenario = self.make_scenario(OTHER_SCENARIO_ID)

    def tearDown(self):
        self.temp.cleanup()

    def make_scenario(self, scenario_id):
        return {
            "scenario_id": scenario_id,
            "identity_sha256": "s" * 64,
            "identity": {
                "bee_composition": ["Wasp"],
                "human_composition": ["Gunship"],
                "sources": [
                    {
                        "batch_id": BATCH_ID,
                        "demo_sha256": self.archive["demo_sha256"] if hasattr(self, "archive") else "d" * 64,
                        "payload_sha256": self.archive["row"]["payload_sha256"] if hasattr(self, "archive") else "p" * 64,
                    }
                ],
            },
        }

    @staticmethod
    def behavior_spec():
        return SimpleNamespace(
            observation_specs=[SimpleNamespace(shape=(OBSERVATION_SIZE,))],
            action_spec=SimpleNamespace(
                continuous_size=CONTINUOUS_ACTIONS,
                discrete_branches=tuple(DISCRETE_BRANCHES),
            ),
        )

    @staticmethod
    def loader_with_count(count):
        pairs = [SimpleNamespace(index=index) for index in range(count)]
        return lambda _path: (AdversarialReplayTests.behavior_spec(), pairs, len(pairs))

    @staticmethod
    def action_reader(pair_info):
        continuous = [0.0] * CONTINUOUS_ACTIONS
        continuous[0] = 1.0
        continuous[2] = 0.0
        continuous[3] = 1.0
        discrete = [0] * 20
        discrete[0] = 1
        return continuous, discrete

    @staticmethod
    def observation_reader(pair_info, _behavior):
        return observation()

    def scenario_lookup(self, scenario_id):
        if scenario_id == SCENARIO_ID:
            return self.scenario
        if scenario_id == OTHER_SCENARIO_ID:
            return self.other_scenario
        raise AssertionError(scenario_id)

    def register(self, **kwargs):
        with patch.object(replay, "_read_scenario", side_effect=self.scenario_lookup), patch.object(
            replay, "_validate_registered_sources"
        ), patch.object(replay, "_approved_archive", return_value=self.archive):
            return replay.register_action_replay(
                self.store,
                SCENARIO_ID,
                source_batch_id=BATCH_ID,
                side=kwargs.pop("side", "Human"),
                record_count=kwargs.pop("record_count", 10),
                **kwargs,
            )

    def compile(self, *, action_reader=None, observation_reader=None):
        with patch.object(replay, "_read_scenario", side_effect=self.scenario_lookup), patch.object(
            replay, "_validate_registered_sources"
        ), patch.object(replay, "_approved_archive", return_value=self.archive):
            return replay.compile_action_replay(
                self.store,
                SCENARIO_ID,
                loader=self.loader_with_count(13),
                observation_reader=observation_reader or self.observation_reader,
                action_reader=action_reader or self.action_reader,
            )

    def test_registration_is_idempotent_but_one_scenario_cannot_change_replay(self):
        first = self.register(record_count=10)
        second = self.register(record_count=10)
        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(first["replay_id"], second["replay_id"])

        with self.assertRaises(ContinualLearningError):
            self.register(record_count=11)

    def test_registration_requires_1v1_scenario_source_membership(self):
        bad = self.make_scenario(SCENARIO_ID)
        bad["identity"]["human_composition"] = ["Gunship", "Frigate"]
        with patch.object(replay, "_read_scenario", return_value=bad), patch.object(
            replay, "_validate_registered_sources"
        ):
            with self.assertRaises(ValidationError):
                replay.register_action_replay(
                    self.store,
                    SCENARIO_ID,
                    source_batch_id=BATCH_ID,
                    side="Human",
                    record_count=10,
                )

        no_source = self.make_scenario(SCENARIO_ID)
        no_source["identity"]["sources"] = []
        with patch.object(replay, "_read_scenario", return_value=no_source), patch.object(
            replay, "_validate_registered_sources"
        ):
            with self.assertRaises(ValidationError):
                replay.register_action_replay(
                    self.store,
                    SCENARIO_ID,
                    source_batch_id=BATCH_ID,
                    side="Human",
                    record_count=10,
                )

    def test_compile_writes_deterministic_header_frames_and_neutral_tail_contract(self):
        self.register(record_count=10)
        result = self.compile()

        payload = Path(result["artifact_path"]).read_bytes()
        magic, frame_count, interval, start_x, start_y = struct.unpack(
            "<8sii2f", payload[:24]
        )
        self.assertEqual(magic, replay.REPLAY_MAGIC)
        self.assertEqual(frame_count, 10)
        self.assertEqual(interval, replay.REPLAY_FIXED_STEP_INTERVAL)
        self.assertAlmostEqual(start_x, 0.0, places=5)
        self.assertAlmostEqual(start_y, 1.0, places=5)
        first_frame = struct.unpack("<34fH", payload[24 : 24 + 138])
        self.assertAlmostEqual(first_frame[0], 1.0)
        self.assertAlmostEqual(first_frame[3], 1.0)
        self.assertEqual(first_frame[-1], 1)
        self.assertTrue(result["truncated"])
        self.assertEqual(result["terminal_behavior"], "neutral")

        metadata = json.loads(Path(result["metadata_path"]).read_text(encoding="utf-8"))
        self.assertEqual(metadata["artifact_sha256"], result["artifact_sha256"])
        self.assertEqual(metadata["frame_count"], 10)

    def test_compile_rejects_capability_or_target_actions(self):
        self.register(record_count=10)

        def special_reader(pair_info):
            continuous, discrete = self.action_reader(pair_info)
            discrete[replay.SPECIAL_ACTION_BRANCH] = 1
            return continuous, discrete

        with self.assertRaises(ValidationError):
            self.compile(action_reader=special_reader)

        def target_reader(pair_info):
            continuous, discrete = self.action_reader(pair_info)
            discrete[replay.ENEMY_TARGET_BRANCH] = 1
            return continuous, discrete

        with self.assertRaises(ValidationError):
            self.compile(action_reader=target_reader)

    def test_compile_rejects_source_ship_identity_mismatch(self):
        self.register(record_count=10)
        with self.assertRaises(ValidationError):
            self.compile(observation_reader=lambda pair, behavior: observation(self_ship=21, enemy_ship=13))

    def test_catalog_uses_relative_artifact_paths_and_ignores_scenarios_without_replay(self):
        self.register(record_count=10)
        with patch.object(replay, "_read_scenario", side_effect=self.scenario_lookup), patch.object(
            replay, "_validate_registered_sources"
        ), patch.object(replay, "_approved_archive", return_value=self.archive):
            result = replay.build_replay_catalog(
                self.store,
                [SCENARIO_ID, OTHER_SCENARIO_ID],
                loader=self.loader_with_count(13),
                observation_reader=self.observation_reader,
                action_reader=self.action_reader,
            )

        self.assertEqual(result["entry_count"], 1)
        entry = result["catalog"]["entries"][0]
        self.assertEqual(entry["scenarioId"], SCENARIO_ID)
        self.assertFalse(Path(entry["replayPath"]).is_absolute())
        self.assertNotIn(str(self.store.root), entry["replayPath"])
        self.assertEqual(result["catalog"]["catalogSha256"], result["catalog_sha256"])


if __name__ == "__main__":
    unittest.main()
