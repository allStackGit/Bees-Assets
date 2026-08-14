# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-014 — Parse shooting matchup without splitting the full strategic key
**Location:** `Scripts/Server/CommandRequest.cs`, `BuildShootingMatchupIdentity()`  
**Cost:** Every Hive Mind `CommandRequest` reconstructs the shooting-policy identity by calling `string.Split('|')` on the full strategic matchup. The request needs only the second segment, so each command request allocates the split array and all parsed segment strings before allocating the required final `actingShips|enemyShips|` result. Dedicated Hive Mind training runs up to 16 simultaneous Levels and repeatedly creates command requests, making this avoidable GC work part of the training command hot path.  
**Optimization:** Locate the first and second delimiters directly and extract only the enemy segment, preserving the exact canonical trailing-delimiter result and current null/malformed-input behavior without materializing the full segment array.  
**Evidence:** `CommandRequest` invokes `BuildShootingMatchupIdentity()` in its constructor for every `GetStrategy` request. The current implementation uses `(strategicMatchup ?? string.Empty).Split('|')` and reads only index 1. Existing `ShootingMatchupProtocolTests` require `ABCDEF|XYZ|1|2 -> ABC|XYZ|` and preservation of an empty enemy segment; repository Hive Mind memory documents the canonical `actingShips|enemyShips|` shape and 16-Level training runtime.  
**Risk:** Preserve the exact second-segment interpretation when there is no delimiter, one delimiter, an empty enemy segment, extra trailing segments, or a null strategic matchup; do not change the protocol's required trailing `|`.

### PERF-015 — Use constant-time membership for explosion hit ships
**Location:** `Scripts/Entities/Projectiles/RocketExplosion.cs`, `_shipsHit` / `HasHitShip()`  
**Cost:** Each pooled rocket/Fire Barge/Fire Tank explosion records damaged ships in a `List<Ship>` and calls `List.Contains` for every candidate collision. Membership therefore becomes linear in the number of ships already hit, so large-area explosions in dense battles repeatedly rescan the growing hit list before applying the existing one-hit policy.  
**Optimization:** Store hit ships in a reusable `HashSet<Ship>` using `ReferenceIdentityComparer<Ship>.Instance`, matching the stable-reference collections already used by other pooled projectile/contact state. Keep clearing the same set on pooled reuse.  
**Evidence:** `RocketExplosion.ShipCollision()` calls `HasHitShip(ship)` on every queued ship contact; `HasHitShip()` delegates to `_shipsHit.Contains`, while `ContactTarget()` only appends and no runtime consumer depends on hit order. The same class already uses a reference-identity `HashSet<Obstacle>` for identical obstacle duplicate suppression, and repository tests codify reference-identity requirements for cross-frame pooled ship sets.  
**Risk:** Preserve exactly-once damage semantics and pooled reset behavior. Do not use `Ship.GetHashCode()`/mutable runtime Id equality for the set because pooled ship wrappers change runtime identity across lives.

### PERF-016 — Replace LINQ minion squad numbering with one linear scan
**Location:** `Scripts/Levels/GameState.Registry.cs`, `AddSquad()`  
**Cost:** Every transient/minion squad registration calculates the next runtime squad number with `Where(...).Select(...).DefaultIfEmpty(...).Max()`. Queen waves and Scout beacon drops create minion squads during live combat, so each spawn constructs LINQ iterator state and traverses the squad collection through multiple iterator layers solely to find the maximum number for one side.  
**Optimization:** Scan `Squads` once, track the maximum `SquadNumber` whose `Side` matches the new minion squad, then assign `max + 1`. This removes the LINQ pipeline while keeping the same O(n) ordering-independent result.  
**Evidence:** `Queen.CreateMinionSquad()` and `Scout.CreateMinionSquad()` set `IsMinionSquad = true` and call `Level.State.AddSquad()`. `AddSquad()` runs the LINQ maximum path for those squads. `TransientSquadNumberingTests` already asserts that normal side-1 squad #1 followed by two minion squads produces runtime numbers 2 and 3 without increasing `OriginalSquadCounts`.  
**Risk:** Preserve side filtering, the empty/default maximum of zero, and the invariant that transient squads do not increment `OriginalSquadCounts` or mark persisted `SavedSquad` ownership.

### PERF-017 — Suppress residual routine runtime logs during training
**Location:** `Scripts/Entities/MapBorder.cs` (`OnTriggerEnter2D()`), `Scripts/Levels/Commands/Charge.cs` (`Timer()`), `Scripts/Entities/Ships/ShipAnimationController.cs` (`ChangeSpriteLoop()`)  
**Cost:** These live simulation paths still build interpolated diagnostic strings and call `Debug.Log` during automated training. MapBorder logs every trigger entry plus additional ship/directional messages, Charge logs each Barge transition into its charging run, and Warp Gate animation readiness logs each completed opening sequence. With multiple simultaneous training Levels, these routine messages add managed string work and Unity logging I/O to physics/command/animation callbacks that do not need informational console output.  
**Optimization:** Guard only these routine informational logs with `!Stage.IsTraining` / `!Ship.Stage.IsTraining`, matching the existing training-log policy elsewhere. Preserve all state changes, warnings, errors, and non-training diagnostics.  
**Evidence:** The branch implementations contain unconditional `Debug.Log` calls directly in `MapBorder.OnTriggerEnter2D()`, `Charge.Timer()`, and `ShipAnimationController.ChangeSpriteLoop()`. Nearby Barge, Level, environment, and command code already suppresses equivalent informational messages during training, while error diagnostics remain active.  
**Risk:** Do not guard the surrounding collision, charge, or Warp Gate state transitions. Only the informational log calls/string interpolation should be skipped in training; normal play/editor diagnostics must remain unchanged.

### PERF-018 — Skip per-lifecycle pooled obstacle naming during training
**Location:** `Scripts/Entities/Obstacle.cs`, `Setup()`  
**Cost:** Every pooled obstacle setup rebuilds an interpolated `ObstacleType #Id` string and writes it to both `Obstacle.Name` and Unity's `gameObject.name`. Collision asteroids can spawn repeatedly throughout a training episode, and destroying one large asteroid can create several pooled asteroid shards plus roughly `1.5 × SizeClass` pooled asteroid pieces, all of which call the same base `Setup()` and pay this diagnostic naming cost despite object reuse.  
**Optimization:** Preserve per-lifecycle descriptive naming outside training, but skip rebuilding/assigning the name when `Stage.IsTraining`, retaining the stable prefab-era name for diagnostics just as pooled projectiles now do.  
**Evidence:** `CollisionAsteroid.Setup()` and `AsteroidPiece.Setup()` both call `base.Setup(level)`. `CollisionAsteroid.SpawnBreakAwayAsteroids()` calls `Setup()` for every shard and asteroid piece. Active obstacle collision, pathfinding, registry, and damage logic uses Id/type/collider/object references rather than `Name`; the direct obstacle-name consumers found are diagnostic/debug text.  
**Risk:** Keep non-training/editor object names unchanged. Do not alter Id assignment, health reset, activation, pathfinder registration, or any randomness/spawn order; training diagnostics may show the stable prefab name instead of a per-lifecycle descriptive name.

Clean static passes: 0 / 2.
