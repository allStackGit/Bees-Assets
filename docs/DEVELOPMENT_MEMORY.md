# Bees development memory

This file records compact, reusable implementation knowledge that is expensive to rediscover. It is not a change log.

## Fire Tank canister visuals

- Runtime object: `Scripts/Entities/CanisterBomb.cs`; serialized object: `Prefabs/Entities/Objects/Fire Tank.prefab`.
- `MapObject.Setup` calls the overridable `InitializeSprite`, and projectile damage calls `OnHealthChanged`. Specialized destructible visuals should use these hooks rather than duplicating `MapObject.OnTriggerEnter2D`.
- Fire Tank body sprites are grouped in `CanisterBomb.Sprites` as contiguous four-frame colour variants: grey stages 0-3, then red stages 0-3. Stage 0 is undamaged; stages advance at 25%, 50%, and 75% health lost.
- Unity sprite references in prefab YAML require both the texture GUID and each sub-sprite internal file ID. Read these from the texture `.meta` file's `nameFileIdTable`; do not use the texture's main file ID.
- `Sprites/Objects/smoke_puff.png` contains five 8x8 frames at 8 pixels per unit. The Fire Tank drives these directly from `CanisterBomb.Update` at 5 FPS; no Animator Controller or animation clip is required.
- Each damage stage after stage 0 enables one additional child `SpriteRenderer`. Smoke plume positions are serialized as `SmokePositions` on the Fire Tank prefab, so visual alignment can be tuned without code changes.
- Smoke children inherit the tank renderer's sorting layer and use sorting order +1. Their animation frame is offset per plume so multiple plumes do not pulse identically.
- When modifying sprite sheets or docs inside the Unity project, ensure the corresponding `.meta` files remain committed so GUIDs stay stable.
