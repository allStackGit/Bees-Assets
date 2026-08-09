# Assets and audio

## Fire Tank visuals and obstacle debris

- Runtime Fire Tank: `Scripts/Entities/CanisterBomb.cs`; serialized object: `Prefabs/Entities/Objects/Fire Tank.prefab`.
- Specialized destructible visuals should use `MapObject.InitializeSprite` and `OnHealthChanged` rather than duplicating projectile-trigger behavior.
- Fire Tank body sprites are four progressive damage stages per colour variant. Smoke uses five 8x8 frames from `Sprites/Objects/smoke_puff.png` at 5 FPS; plume positions are serialized so visual alignment can be tuned without code changes.
- Fire Tank destruction flows through `CanisterBomb.Kill()` -> `Obstacle.BreakApart(...)` -> normal `Obstacle.Kill()`.
- Obstacle debris is cosmetic only: no collider/Rigidbody gameplay interaction. `ObstacleDebrisPiece` owns transform motion/spin/damping/fade and uses deterministic local randomness so cosmetic effects do not advance the global Unity random state.
- If repeated cosmetic debris becomes a profiling problem, pool `ObstacleDebrisPiece` rather than adding gameplay state to it.

## Audio ownership

- Keep the existing two-tier architecture: persistent `UIAudioController` owns non-spatial UI/menu/notification audio; Stage/gameplay audio owns weapons, explosions, ships, and world effects.
- UI feedback should use `AudioSource.PlayOneShot` so rapid interactions do not restart/truncate the shared source.
- Short UI clips should preload to avoid first-use latency.
- Delete-squad feedback belongs on confirmed deletion, not merely button press.
- Error feedback is centralized through blocked/error `Alert` presentation rather than scattered through every failed action return path.
- Intercom plays once when a new dialogue section starts, not on each continued line.
- Level-intro engine hum is looping ambience scoped to that screen; it is additive unless design explicitly replaces menu music.
- User-save feedback belongs at explicit UI save completion. Generic campaign/autosave persistence stays silent.
- Fire Tank explosion audio reuses the gameplay explosion/mixer path and must be explicitly replayed when the pooled explosion object fires.
