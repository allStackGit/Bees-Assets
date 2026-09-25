from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import bees_training_diagnostic_benchmark as benchmark
from bees_continual_evaluate import MatchSummary


class DiagnosticBenchmarkTests(unittest.TestCase):
    def test_benchmark_uses_same_model_deterministically_and_collects_aim_metrics(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            environment = root / "Bees.exe"
            model = root / "policy.onnx"
            environment.write_bytes(b"env")
            model.write_bytes(b"model")
            calls = []

            def fake_runner(**kwargs):
                calls.append(kwargs)
                env_args = list(kwargs["env_args"])
                log_index = env_args.index("-logFile") + 1
                log_path = Path(env_args[log_index])
                log_path.write_text(
                    "RL 1v1 episode=1 timeout=False duration=10.0s "
                    "bee_tsv=16->16 human_tsv=18->0 "
                    "bee_fire_requests=2 bee_shots=2 bee_hits=1 bee_damage=15 "
                    "human_fire_requests=1 human_shots=1 human_hits=0 human_damage=0 "
                    "bee_aim_samples=2 bee_aim_error=20.00deg "
                    "bee_aim_within_5deg=50.00% bee_turret_aligned=100.00% "
                    "human_aim_samples=1 human_aim_error=80.00deg "
                    "human_aim_within_5deg=0.00% human_turret_aligned=100.00%\n"
                    "RL 1v1 episode=2 timeout=True duration=30.0s "
                    "bee_tsv=16->16 human_tsv=18->18 "
                    "bee_fire_requests=1 bee_shots=1 bee_hits=0 bee_damage=0 "
                    "human_fire_requests=2 human_shots=2 human_hits=0 human_damage=0 "
                    "bee_aim_samples=1 bee_aim_error=50.00deg "
                    "bee_aim_within_5deg=0.00% bee_turret_aligned=100.00% "
                    "human_aim_samples=2 human_aim_error=70.00deg "
                    "human_aim_within_5deg=0.00% human_turret_aligned=50.00%\n",
                    encoding="utf-8",
                )
                return MatchSummary(
                    matches=2,
                    wins=1,
                    losses=0,
                    draws=1,
                    timeouts=1,
                    total_duration_seconds=40.0,
                    candidate_starting_tsv=32,
                    candidate_final_tsv=32,
                    candidate_shots=3,
                    candidate_hits=1,
                    candidate_damage=15,
                    opponent_starting_tsv=36,
                    opponent_final_tsv=18,
                    opponent_shots=3,
                    opponent_hits=0,
                    opponent_damage=0,
                    telemetry_validated=True,
                )

            value = benchmark.run_benchmark(
                environment_path=environment,
                model_path=model,
                matches=2,
                match_runner=fake_runner,
            )

            self.assertEqual(len(calls), 1)
            call = calls[0]
            self.assertEqual(
                Path(call["candidate_model_path"]),
                model.resolve(),
            )
            self.assertEqual(
                Path(call["opponent_model_path"]),
                model.resolve(),
            )
            self.assertIn("--bees-rl-arenas-per-env=1", call["env_args"])
            self.assertIn("--rl-bee-ship-types=Wasp", call["env_args"])
            self.assertIn("--rl-human-ship-types=Gunship", call["env_args"])
            self.assertEqual(value["status"], "succeeded")
            self.assertTrue(value["deterministic_actions"])
            self.assertEqual(value["summary"]["matches"], 2)
            self.assertAlmostEqual(value["derived"]["candidate_hits_per_shot"], 1 / 3)
            self.assertEqual(value["aim_metrics"]["window_episodes"], 2)
            self.assertEqual(value["aim_metrics"]["bee_aim_samples"], 3)
            self.assertEqual(value["aim_metrics"]["human_aim_samples"], 3)
            self.assertAlmostEqual(
                value["aim_metrics"]["bee_aim_error_deg"],
                30.0,
                places=2,
            )
            self.assertAlmostEqual(
                value["aim_metrics"]["human_aim_error_deg"],
                220.0 / 3.0,
                places=2,
            )

    def test_missing_environment_fails_before_runner(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            model = root / "policy.onnx"
            model.write_bytes(b"model")
            with self.assertRaises(FileNotFoundError):
                benchmark.run_benchmark(
                    environment_path=root / "missing.exe",
                    model_path=model,
                    match_runner=lambda **kwargs: self.fail("runner should not execute"),
                )


if __name__ == "__main__":
    unittest.main()
