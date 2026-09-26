"""Static regression coverage for the persistent training worker's log metrics."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import bees_training_worker_agent as worker


class EpisodeLogMetricsTests(unittest.TestCase):
    def test_initial_bounded_scan_keeps_episode_when_tail_starts_on_line_boundary(self):
        tail_bytes = 4 * 1024 * 1024
        episode = (
            "RL 1v1 episode=1 timeout=False duration=10s "
            "bee_tsv=100->0 human_tsv=100->0 "
            "bee_fire_requests=1 bee_shots=1 bee_hits=1 bee_damage=1 "
            "human_fire_requests=0 human_shots=0 human_hits=0 human_damage=0\n"
        ).encode("ascii")
        prefix = b"x" * (tail_bytes - 1) + b"\n"
        padding = b"x" * (tail_bytes - len(episode))
        self.assertGreaterEqual(len(padding), 0)

        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "Player-0.log").write_bytes(prefix + episode + padding)
            metrics = worker.EpisodeLogMetrics(Path(directory))

            snapshot = metrics.refresh()

        self.assertEqual(snapshot["window_episodes"], 1)
        self.assertEqual(snapshot["last_episode"], 1)


if __name__ == "__main__":
    unittest.main()
