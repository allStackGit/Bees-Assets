from __future__ import annotations

import json
import os
import tempfile
import time
import unittest
import zipfile
from pathlib import Path

import bees_training_bundle as bundle


class TrainingBundleTests(unittest.TestCase):
    def _layout(self, root: Path, run_id: str) -> tuple[Path, Path]:
        bees_root = root / "Bees"
        assets_root = bees_root / "Assets"
        (assets_root / "Training").mkdir(parents=True)
        (assets_root / "Training" / "bees.cluster.json").write_text(
            '{"numLocalEnvs": 2}\n',
            encoding="utf-8",
        )
        (assets_root / "Training" / "rl_1v1_config.yaml").write_text(
            "behaviors: {}\n",
            encoding="utf-8",
        )
        lifecycle = bees_root / "Training" / "RunLifecycle"
        lifecycle.mkdir(parents=True)
        (lifecycle / "current.json").write_text(
            json.dumps({"run_id": run_id, "compatibility_key": "a" * 64}),
            encoding="utf-8",
        )
        return bees_root, assets_root

    def test_bundle_collects_latest_model_log_tails_remote_logs_and_timers(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run_id = "bees-v19-test"
            bees_root, assets_root = self._layout(root, run_id)

            server_logs = bees_root / "Logs" / "Server"
            learner_logs = bees_root / "Logs" / "Training"
            remote_logs = (
                bees_root
                / "Training"
                / "TrainerLogs"
                / run_id
                / "Warwick"
            )
            server_logs.mkdir(parents=True)
            learner_logs.mkdir(parents=True)
            remote_logs.mkdir(parents=True)
            (server_logs / "bees-server.log").write_text(
                "\n".join(f"server-{index:03d}" for index in range(100)),
                encoding="utf-8",
            )
            (learner_logs / "central-agent.out.log").write_text(
                "\n".join(f"central-{index:03d}" for index in range(100)),
                encoding="utf-8",
            )
            (remote_logs / "Player-0.log").write_text(
                "\n".join(f"remote-{index:03d}" for index in range(100)),
                encoding="utf-8",
            )

            results = bees_root / "Training" / "trainer-results" / run_id
            behavior = results / "BeesRL1v1"
            behavior.mkdir(parents=True)
            old_model = behavior / "old.onnx"
            new_model = behavior / "new.onnx"
            old_model.write_bytes(b"old-model")
            new_model.write_bytes(b"new-model")
            now = time.time()
            os.utime(old_model, (now - 2, now - 2))
            os.utime(new_model, (now - 1, now - 1))
            run_logs = results / "run_logs"
            run_logs.mkdir()
            (run_logs / "timers.json").write_text(
                '{"total": 12}\n',
                encoding="utf-8",
            )

            status_json = root / "status.json"
            status_json.write_text(
                json.dumps({"desired": {"run_id": run_id}, "trainers": []}),
                encoding="utf-8",
            )
            status_text = root / "status.txt"
            status_text.write_text("Server: ONLINE\n", encoding="utf-8")

            archive = bundle.create_bundle(
                bees_root=bees_root,
                assets_root=assets_root,
                log_percent=10.0,
                status_json=status_json,
                status_text=status_text,
            )

            with zipfile.ZipFile(archive) as zipped:
                names = set(zipped.namelist())
                self.assertIn("model/new.onnx", names)
                self.assertNotIn("model/old.onnx", names)
                self.assertIn("timers/run_logs/timers.json", names)
                self.assertIn(
                    "logs/trainers/Warwick/Player-0.log",
                    names,
                )
                combined = zipped.read("combined-logs.txt").decode("utf-8")
                self.assertIn("server-099", combined)
                self.assertIn("central-099", combined)
                self.assertIn("remote-099", combined)
                self.assertNotIn("server-000", combined)
                manifest = json.loads(zipped.read("manifest.json"))
                self.assertEqual(run_id, manifest["run_id"])
                self.assertEqual(10.0, manifest["log_percent"])
                self.assertEqual(
                    "model/new.onnx",
                    manifest["latest_onnx"]["archive_path"],
                )

    def test_live_snapshot_is_preferred_and_manifest_flags_trainer_health(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run_id = "bees-v20-health"
            bees_root, assets_root = self._layout(root, run_id)
            (assets_root / "Training" / "bees.cluster.json").write_text(
                json.dumps({"expectedTrainers": ["central-learner", "remote-warwick"]}),
                encoding="utf-8",
            )

            results = bees_root / "Training" / "trainer-results" / run_id / "BeesRL1v1"
            results.mkdir(parents=True)
            fallback = results / "BeesRL1v1-100.onnx"
            fallback.write_bytes(b"fallback")
            live = results / "diagnostic-BeesRL1v1-9950-abc.onnx"
            live.write_bytes(b"live")

            trainer_log = (
                bees_root
                / "Training"
                / "TrainerLogs"
                / run_id
                / "remote-warwick"
                / "remote-supervisor.log"
            )
            trainer_log.parent.mkdir(parents=True)
            trainer_log.write_text("worker output\n", encoding="utf-8")

            status_json = root / "status.json"
            status_json.write_text(
                json.dumps(
                    {
                        "desired": {
                            "run_id": run_id,
                            "canonical_build_id": "build-current",
                            "revision": 9,
                            "training_enabled": True,
                        },
                        "trainers": [
                            {
                                "trainer_id": "central-learner",
                                "role": "dedicated",
                                "process_state": "running",
                                "stale": False,
                                "build_id": "build-current",
                                "applied_revision": 9,
                                "age_seconds": 1.0,
                                "last_error": "",
                            },
                            {
                                "trainer_id": "remote-warwick",
                                "role": "dedicated",
                                "process_state": "running",
                                "stale": True,
                                "build_id": "build-old",
                                "applied_revision": 8,
                                "age_seconds": 45.0,
                                "last_error": "No space left on device",
                            },
                        ],
                    }
                ),
                encoding="utf-8",
            )
            status_text = root / "status.txt"
            status_text.write_text(
                "Learner logs: Step=10000  ELO=1000\n",
                encoding="utf-8",
            )
            snapshot_json = root / "snapshot.json"
            snapshot_json.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "status": "succeeded",
                        "request_id": "abc",
                        "run_id": run_id,
                        "step": 9950,
                        "model_path": str(live),
                    }
                ),
                encoding="utf-8",
            )

            archive = bundle.create_bundle(
                bees_root=bees_root,
                assets_root=assets_root,
                log_percent=10.0,
                status_json=status_json,
                status_text=status_text,
                snapshot_json=snapshot_json,
            )

            with zipfile.ZipFile(archive) as zipped:
                manifest = json.loads(zipped.read("manifest.json"))
                self.assertEqual(manifest["learner_step"], 10000)
                self.assertEqual(manifest["model_step"], 9950)
                self.assertEqual(manifest["model_lag_steps"], 50)
                self.assertEqual(
                    manifest["latest_onnx"]["selection"],
                    "live-snapshot",
                )
                self.assertEqual(
                    zipped.read("model/" + live.name),
                    b"live",
                )
                warnings = "\n".join(manifest["warnings"])
                self.assertIn("stale", warnings)
                self.assertIn("No space left on device", warnings)
                self.assertIn("build mismatch", warnings)
                self.assertIn("revision mismatch", warnings)
                self.assertIn(
                    "remote-supervisor.log",
                    zipped.namelist(),
                )

    def test_stale_fallback_model_produces_model_lag_warning(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run_id = "bees-v20-lag"
            bees_root, assets_root = self._layout(root, run_id)
            results = bees_root / "Training" / "trainer-results" / run_id / "BeesRL1v1"
            results.mkdir(parents=True)
            (results / "BeesRL1v1-100.onnx").write_bytes(b"old")
            status_text = root / "status.txt"
            status_text.write_text("Learner logs: Step=10000\n", encoding="utf-8")

            archive = bundle.create_bundle(
                bees_root=bees_root,
                assets_root=assets_root,
                log_percent=10.0,
                status_text=status_text,
            )

            with zipfile.ZipFile(archive) as zipped:
                manifest = json.loads(zipped.read("manifest.json"))
                self.assertEqual(manifest["model_lag_steps"], 9900)
                self.assertTrue(
                    any(
                        "9900 learner steps behind" in warning
                        for warning in manifest["warnings"]
                    )
                )

    def test_unified_operator_exposes_bundle_command(self) -> None:
        operator = (
            Path(__file__).resolve().parents[1] / "bees.ps1"
        ).read_text(encoding="utf-8-sig")
        self.assertIn(
            "ValidateSet('build','server','start','stop','status','bundle')",
            operator,
        )
        self.assertIn("$DiagnosticBundleScript=", operator)
        self.assertIn("$CentralModelSnapshotRequestPath=", operator)
        self.assertIn("Request-CentralDiagnosticModelSnapshot", operator)
        self.assertIn("--snapshot-json", operator)
        self.assertIn("'bundle'{Invoke-Bundle}", operator)

    def test_missing_onnx_is_a_warning_not_a_bundle_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run_id = "bees-early-run"
            bees_root, assets_root = self._layout(root, run_id)
            (bees_root / "Logs" / "Training").mkdir(parents=True)
            (bees_root / "Logs" / "Training" / "central.log").write_text(
                "startup\n",
                encoding="utf-8",
            )

            archive = bundle.create_bundle(
                bees_root=bees_root,
                assets_root=assets_root,
                log_percent=25.0,
            )

            with zipfile.ZipFile(archive) as zipped:
                manifest = json.loads(zipped.read("manifest.json"))
                self.assertIsNone(manifest["latest_onnx"])
                self.assertTrue(
                    any(
                        "no ONNX file found" in warning
                        for warning in manifest["warnings"]
                    )
                )


if __name__ == "__main__":
    unittest.main()
