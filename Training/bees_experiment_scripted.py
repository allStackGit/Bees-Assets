"""Versioned, deterministic Bees evaluation opponents.

The policies read only Unity's existing RlCombatPerception vector observation.
No ground-truth enemy position, Unity object, RNG, or training code is used.
The numeric observation/action layout is the frozen BeesRL1v1 v26 ABI.
"""
from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Any, Iterable

import numpy as np

SCRIPT_VERSION = 1
MODES = ("stationary", "strafe", "pursuit")
SELF_SIZE = 25
CAPABILITY_SIZE = 12
ENTITY_SIZE = 41
PARENT_SIZE = ENTITY_SIZE
ALLY_SIZE = ENTITY_SIZE + 4
MAX_ALLIES = 64
MAX_ENEMIES = 64
ENEMY_OFFSET = SELF_SIZE + CAPABILITY_SIZE + PARENT_SIZE + MAX_ALLIES * ALLY_SIZE
WEAPON_OFFSET = ENEMY_OFFSET + MAX_ENEMIES * ENTITY_SIZE
WEAPON_SIZE = 15
MAX_WEAPONS = 5
CONTINUOUS_ACTIONS = 2 + 2 * MAX_WEAPONS + 4
DISCRETE_BRANCHES = (2, 2, 2, 2, 2, 5)
DISTANCE_SCALE = 40.0

# v26 entity layout: present, identity, relative_x/y, heading_x/y,
# health, speed, current speed, geometry/range/capability, type and mounts.
ENEMY_PRESENT = 0
ENEMY_RELATIVE_X = 2
ENEMY_RELATIVE_Y = 3
# Self layout: identity, type, normalized position x/y, map extent x/y,
# heading x/y, stats, and whether movement is enabled at index 16.
SELF_POSITION_X = 2
SELF_POSITION_Y = 3
SELF_MAP_X = 4
SELF_MAP_Y = 5
SELF_MOVEMENT_ENABLED = 16
SELF_LONGEST_SIDE = 12
# Weapon layout: present, type, offset x/y, range, power, rate,
# rotation, projectile value/speed, turret flag, turret heading, readiness.
WEAPON_PRESENT = 0
WEAPON_OFFSET_X = 2
WEAPON_OFFSET_Y = 3
WEAPON_RANGE = 4
WEAPON_TURRET = 10


def _unsquash_signed(value: float) -> float:
    # RlCombatPerception.SquashSignedDistance: sign(x)*abs(x)/(abs(x)+40).
    value = max(-0.99999, min(0.99999, float(value)))
    return math.copysign(DISTANCE_SCALE * abs(value) / (1.0 - abs(value)), value)


def _positive(value: float, scale: float) -> float:
    # RlCombatPerception.NormalizePositive(x,scale) = x/(x+scale).
    value = max(0.0, min(0.99999, float(value)))
    return scale * value / (1.0 - value)


def _unit(x: float, y: float) -> tuple[float, float]:
    length = math.hypot(x, y)
    if length < 1e-6:
        return (0.0, 0.0)
    return (x / length, y / length)


@dataclass
class _AgentMemory:
    waypoint: int = 0
    last_seen: tuple[float, float] | None = None


class ScriptedPolicy:
    """Deterministic benchmark controller with the same actions as BeesRL1v1."""

    def __init__(self, mode: str) -> None:
        if mode not in MODES:
            raise ValueError(f"Unknown scripted opponent {mode!r}; choose {MODES}.")
        self.mode = mode
        self.memory: dict[int, _AgentMemory] = {}
        self.inference_calls = 0
        self.inference_total_seconds = 0.0
        self.inference_max_seconds = 0.0

    def validate_behavior_spec(self, behavior_spec: Any) -> None:
        shapes = [tuple(spec.shape) for spec in behavior_spec.observation_specs]
        actions = behavior_spec.action_spec
        if (len(shapes) != 1 or len(shapes[0]) != 1 or
                shapes[0][0] < WEAPON_OFFSET + MAX_WEAPONS * WEAPON_SIZE or
                int(actions.continuous_size) != CONTINUOUS_ACTIONS or
                tuple(int(n) for n in actions.discrete_branches) != DISCRETE_BRANCHES):
            raise ValueError(
                f"Scripted opponent requires BeesRL1v1 v26 observations/actions; "
                f"observations={shapes}, continuous={actions.continuous_size}, "
                f"branches={tuple(actions.discrete_branches)}."
            )

    def forget(self, agent_ids: Iterable[int]) -> None:
        for agent_id in agent_ids:
            self.memory.pop(int(agent_id), None)

    def actions(self, decision_steps: Any, behavior_spec: Any, ActionTuple: Any) -> Any:
        self.validate_behavior_spec(behavior_spec)
        count = len(decision_steps)
        if not count:
            return ActionTuple()
        if len(decision_steps.obs) != 1:
            raise ValueError("Scripted policy requires one vector observation.")
        vectors = np.asarray(decision_steps.obs[0], dtype=np.float32)
        if vectors.shape != (count, behavior_spec.observation_specs[0].shape[0]):
            raise ValueError(f"Unexpected scripted policy observation batch {vectors.shape}.")
        if not np.isfinite(vectors).all():
            raise ValueError("Non-finite scripted-policy observations.")
        continuous = np.zeros((count, CONTINUOUS_ACTIONS), dtype=np.float32)
        discrete = np.zeros((count, len(DISCRETE_BRANCHES)), dtype=np.int32)
        started = time.perf_counter()
        for row, agent_id in enumerate(decision_steps.agent_id):
            agent_memory = self.memory.setdefault(int(agent_id), _AgentMemory())
            self._act(vectors[row], agent_memory, continuous[row], discrete[row],
                      decision_steps.action_mask, row)
        elapsed = time.perf_counter() - started
        self.inference_calls += 1
        self.inference_total_seconds += elapsed
        self.inference_max_seconds = max(self.inference_max_seconds, elapsed)
        return ActionTuple(continuous=continuous, discrete=discrete)

    @staticmethod
    def _allowed_fire(mask: Any, slot: int, row: int) -> bool:
        # ML-Agents action_mask is True for disallowed actions.
        if mask is None or mask[slot] is None:
            return True
        return not bool(mask[slot][row, 1])

    def _act(self, obs: np.ndarray, memory: _AgentMemory,
             continuous: np.ndarray, discrete: np.ndarray,
             action_mask: Any, row: int) -> None:
        enemy = obs[ENEMY_OFFSET:ENEMY_OFFSET + ENTITY_SIZE]
        seen = enemy[ENEMY_PRESENT] > 0.5
        self_xy = np.array([
            float(obs[SELF_POSITION_X]), float(obs[SELF_POSITION_Y])
        ], dtype=float)
        extent = np.array([
            _positive(obs[SELF_MAP_X], 100.0), _positive(obs[SELF_MAP_Y], 100.0)
        ], dtype=float)
        own_position = self_xy * extent * 0.5

        target: tuple[float, float] | None = None
        distance = float("inf")
        if seen:
            dx = _unsquash_signed(enemy[ENEMY_RELATIVE_X])
            dy = _unsquash_signed(enemy[ENEMY_RELATIVE_Y])
            target = (dx, dy)
            distance = math.hypot(dx, dy)
            memory.last_seen = (own_position[0] + dx, own_position[1] + dy)
        elif memory.last_seen is not None:
            dx = memory.last_seen[0] - own_position[0]
            dy = memory.last_seen[1] - own_position[1]
            if math.hypot(dx, dy) > 2.0:
                target = (dx, dy)
                distance = math.hypot(dx, dy)
            else:
                memory.last_seen = None

        longest_side = max(1.0, _positive(obs[SELF_LONGEST_SIDE], 10.0))
        preferred_range = 40.0
        found_range = False
        if seen:
            for slot in range(MAX_WEAPONS):
                weapon = obs[
                    WEAPON_OFFSET + slot * WEAPON_SIZE:
                    WEAPON_OFFSET + (slot + 1) * WEAPON_SIZE
                ]
                if weapon[WEAPON_PRESENT] < 0.5 or weapon[WEAPON_TURRET] < 0.5:
                    continue
                weapon_range = _positive(weapon[WEAPON_RANGE], 80.0)
                preferred_range = (min(preferred_range, weapon_range)
                                   if found_range else weapon_range)
                found_range = True
                aim_x = target[0] - float(weapon[WEAPON_OFFSET_X]) * longest_side
                aim_y = target[1] - float(weapon[WEAPON_OFFSET_Y]) * longest_side
                aiming = _unit(aim_x, aim_y)
                continuous[2 + slot * 2:4 + slot * 2] = aiming
                # The physical turret itself honors turning/alignment/reload.
                # No shot is requested when no enemy is observed or out of range.
                if (math.hypot(aim_x, aim_y) <= weapon_range and
                        self._allowed_fire(action_mask, slot, row)):
                    discrete[slot] = 1

        if self.mode == "stationary" or obs[SELF_MOVEMENT_ENABLED] <= 0.5:
            return

        if self.mode == "strafe":
            if target is not None:
                ux, uy = _unit(*target)
                # Fixed counterclockwise orbit; close distance only if needed.
                forward = 0.65 if distance > preferred_range * 0.75 else 0.0
                dx, dy = _unit(-uy + ux * forward, ux + uy * forward)
            else:
                dx, dy = _unit(-own_position[0], -own_position[1])
        else:  # pursuit
            if target is not None:
                ux, uy = _unit(*target)
                if distance > preferred_range * 0.7:
                    dx, dy = ux, uy
                else:
                    dx, dy = _unit(-uy * 0.65 + ux * 0.2, ux * 0.65 + uy * 0.2)
            else:
                # Deterministic search using only own location/map dimensions.
                # No enemy position is supplied before contact.
                points = ((0.5, 0.5), (-0.5, 0.5), (-0.5, -0.5), (0.5, -0.5))
                wx, wy = points[memory.waypoint % len(points)]
                destination = (wx * extent[0] * 0.5, wy * extent[1] * 0.5)
                if math.hypot(destination[0] - own_position[0],
                              destination[1] - own_position[1]) < 5:
                    memory.waypoint += 1
                    wx, wy = points[memory.waypoint % len(points)]
                    destination = (wx * extent[0] * 0.5, wy * extent[1] * 0.5)
                dx, dy = _unit(destination[0] - own_position[0],
                               destination[1] - own_position[1])

        # Pull back from lethal borders. Existing Unity physics owns collision.
        if max(abs(self_xy[0]), abs(self_xy[1])) > 0.70:
            inward = _unit(-float(self_xy[0]), -float(self_xy[1]))
            dx, dy = _unit(dx * 0.25 + inward[0], dy * 0.25 + inward[1])
        continuous[:2] = (dx, dy)
