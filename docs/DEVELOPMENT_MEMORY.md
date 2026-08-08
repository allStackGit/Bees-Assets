# Bees development memory

This file records compact, reusable implementation knowledge that is expensive to rediscover. It is not a change log.

## Fire Tank canister visuals

- Runtime object: `Scripts/Entities/CanisterBomb.cs`; serialized object: `Prefabs/Entities/Objects/Fire Tank.prefab`.
- `MapObject.Setup` calls the overridable `InitializeSprite`, and projectile damage calls `OnHealthChanged`. Specialized destructible visuals should use these hooks rather than duplicating `MapObject.OnTriggerEnter2D`.
- Fire Tank body sprites are grouped in `CanisterBomb.Sprites` as contiguous four-frame colour variants: grey stages 0-3, then red stages 0-3. Stage 0 is undamaged; stages advance at 25%, 50%, and 75% health lost.
- Unity sprite references in prefab YAML require both the texture GUID and each sub-sprite internal file ID. Read these from the texture `.meta` file's `nameFileIdTable`; do not use the texture's main file ID.
- `Sprites/Objects/smoke_puff.png` contains five 8x8 frames at 8 pixels per unit. The Fire Tank drives these directly from `CanisterBomb.Update` at 5 FPS; no Animator Controller or animation clip is required.
- Each damage stage after stage 0 enables one additional child `SpriteRenderer`. Smoke plume positions are serialized as `SmokePositions` on the Fire Tank prefab, so visual alignment can be tuned without code changes.
- The four Fire Tank frames are 530x165 at 40 pixels per unit. To convert a damage point from sheet-local pixels into prefab-local coordinates, use `x = (pixelX - 265) / 40` and `y = (82.5 - pixelY) / 40`. Current plume order follows the accumulating artwork: lower-right rupture, upper-shell damage, then the large left-side breach.
- Smoke children inherit the tank renderer's sorting layer and use sorting order +1. Their animation frame is offset per plume so multiple plumes do not pulse identically.

## Obstacle destruction debris

- Fire Tank obstacle destruction flows through `CanisterBomb.Kill()` -> `Obstacle.BreakApart(...)` -> normal `Obstacle.Kill()`. Keep the breakup geometry on `Obstacle`; callers provide the explosion origin, debris sprite set, and tuning values.
- `Sprites/Obstacles/scrap_bits.png` contains 43 sliced fragment sprites. The current imported sub-sprites use lower-left pivots, so `ObstacleDebrisPiece` puts the `SpriteRenderer` on a centered child (`localPosition = -sprite.bounds.center`) and rotates the root. This avoids editing all sprite pivots and prevents visible orbiting during spin.
- Cosmetic obstacle fragments deliberately have no collider and no `Rigidbody2D`. `ObstacleDebrisPiece` moves/rotates its transform, exponentially damps velocity, fades after 60% of its lifetime, and destroys itself. This prevents debris from affecting combat, pathfinding, or physics.
- Debris spawn positions come from the destroyed obstacle collider bounds (renderer bounds are the fallback), so fragments appear to originate throughout the obstacle rather than from the explosion center. Launch direction is radial from the supplied explosion position with limited random spread.
- Debris randomness uses a local `System.Random` seeded from obstacle ID and explosion position. Do not use `UnityEngine.Random` for purely cosmetic debris because advancing the global Unity random state can interfere with deterministic simulation/replay work.
- Fire Tank debris tuning is serialized on `Prefabs/Entities/Objects/Fire Tank.prefab`: count, speed range, max spin, lifetime, damping, and scale range. Tune these visually before changing the algorithm.
- `AsteroidPiece` is the existing precedent for non-interacting destruction fragments, but Fire Tank obstacle debris is intentionally lighter because it does not need obstacle registration, Rigidbody2D physics, timers, or pooling.
- The current implementation instantiates/destroys cosmetic debris because Fire Tank detonations are infrequent. If profiling shows repeated explosions causing allocation/GC spikes, integrate `ObstacleDebrisPiece` with `Pool` rather than adding gameplay state to the fragments.

## Unity asset handling

- When modifying sprite sheets or docs inside the Unity project, ensure the corresponding `.meta` files remain committed so GUIDs stay stable.
