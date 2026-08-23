# Bees-Assets Model Onboarding Primer

> **Purpose:** This is an opt-in context-transfer document for a model or human who is new to Bees-Assets or needs a broad re-orientation. It is deliberately **not** part of routine required reading. Do not load it for ordinary development tasks unless the user explicitly asks for cold-start onboarding, broad re-orientation, or use of the primer.
>
> **Snapshot:** 2026-08-17. This is a curated project model, not runtime authority. Before changing behavior, follow the current root `AGENTS.md`, start from `docs/engineering/CONTEXT_INDEX.md`, and verify relevant details against current C# source, Unity scenes/prefabs/assets, configuration, tests, and—when a cross-repository contract is involved—the current BeesServer repository.

## 1. Project in one paragraph

Bees is a real-time 2D fleet-tactics game built in Unity. It combines persistent fleets and squads, pooled runtime ships and combat objects, a multi-mission campaign with strategic consequences, local/server-backed profile state, multiple battle modes, asynchronous pathfinding, replay/determinism work, responsive screen-space UI, authored maps/obstacles/prefabs, and a server-backed **Hive Mind** system that selects and learns targeting, strategic-command, and shooting behavior from real gameplay outcomes.

The project cannot be understood safely as “just C# scripts.” Correct behavior is jointly defined by code, serialized Unity objects, scenes, prefab names/GUIDs, Resources paths, runtime lifecycle ownership, persistence state, server contracts, and authored campaign geometry.

## 2. How a new model should use this file

For deliberate cold-start onboarding:

1. Read this primer once to build the broad mental model.
2. Read the current root `AGENTS.md` before doing repository work.
3. Follow `AGENTS.md`'s normal lightweight route: `AGENTS.md -> docs/engineering/CONTEXT_INDEX.md -> relevant owner docs/source/assets/tests`.
4. Do not preload this primer again for every task.
5. Revalidate material details against current source/assets before editing.
6. If a change touches a wire, persistence, Hive Mind learning, reconnect, database/version, or shared Game contract, inspect the current BeesServer repository as well.

Routine repository learning belongs in the smaller maintained owner documents. This primer exists to make accumulated project understanding portable across models and humans.

## 3. Stable product and engineering contracts

The project constitution establishes several high-level rules that ordinary fixes must preserve:

- persistent fleet/squad/campaign/settings state is real gameplay state and must remain attached to the correct user/mode/level/entity;
- runtime mutable state belongs to its current `Level`, `GameState`, request, and pooled-object lifetime;
- pooled objects must behave as new logical objects when reacquired;
- asynchronous/background results may publish only to the request and object lifetime that still owns them;
- scene names, map names, Resources paths, prefab identities and serialized references are runtime contracts;
- campaign behavior is multi-source and must not be inferred from one trigger file, stale JSON file, save snapshot, or prefab in isolation;
- Unity/BeesServer request, response, persistence, reconnect and learning contracts are cross-repository contracts;
- deterministic evidence cannot depend on unordered collection iteration or incidental cosmetic random-state mutation;
- optimization may not remove cleanup, synchronization, intended gameplay, validation, persistence/network compatibility, or default quality;
- explicitly incomplete campaign content must not be fabricated into a “completed” state merely to satisfy tests or an agent's expectations.

When current implementation contradicts an established invariant, treat the implementation as suspect unless the project owner deliberately changes the intended behavior.

## 4. Runtime ownership hierarchy

The most useful high-level runtime model is:

`ConfigData -> Scene/Stage -> one or more Level -> one GameState per Level -> persistent squad/fleet data -> pooled runtime Squad/Ship/Weapon/Command/projectile objects`

### `ConfigData`

`ConfigData` is global reach. It owns or exposes configuration, enums, selected mode/data handles, settings, user-data bootstrap and lazy socket access. A change here can affect startup, persistence, networking, tests and scene behavior simultaneously.

Configuration reads deliberately do **not** open the game WebSocket; socket creation is lazy on first real socket use. This keeps offline tools/tests deterministic and avoids network side effects during mere configuration access.

### `Scene` / `Stage`

`Scene` and especially `Stage` coordinate Unity scene lifecycle, data/network readiness, pools, prefabs, UI, input/audio/camera and one or more active Levels.

One Unity connection can serve multiple Levels in some modes. Do not equate “current Stage” or “current socket” with ownership of every individual Level mutation.

### `Level`

A `Level` is one battle arena and lifecycle owner. It contains options, map/environment, timers, objectives, server game context, saving/teardown behavior and its own `GameState`.

A Level is the primary local ownership unit even when multiple sibling Levels share a server-side Hive Mind `Game` through one connection.

### `GameState`

`GameState` is the authoritative per-Level runtime registry. It owns active runtime object indexes, visibility/selection state, commands/outcomes, dynamic state, counters, score/game-over flags, request-related ownership and deferred releases.

`GameState.ResetState` is therefore a high-risk cleanup boundary rather than a cosmetic reset helper.

## 5. Identity namespaces: one of the most important project concepts

Bees contains multiple kinds of IDs that may all look like integers or strings but mean different things.

### Account/user identity

User identity is the profile/backend identity used between Unity and BeesServer. Steam64-sized IDs must retain exact integer meaning; server-side JavaScript must not accidentally round them through ordinary `Number` semantics.

### Persistent fleet identity

Persistent identity includes:

- `FleetShip.Id`
- `SavedSquad.Id`
- `SquadShip.FleetId`

Generated/transient persistent records may use negative IDs. These are profile/campaign identities, not pooled runtime IDs.

### Runtime pooled identity

Runtime identities include:

- `Squad.ItemId`
- runtime `Ship.Id`
- command/weapon/object IDs allocated through current `GameState`/`Pool`

They are valid only for the current Level/object lifetime. A recycled Squad can later have a different logical life even if the underlying Unity object instance is the same.

### Request identity

Network request `Hash` values belong to standing requests and their owning Level/lifecycle. They participate in resend/deduplication and must be removed only by the lifecycle that owns them.

### Learning OutcomeId

Hive Mind `OutcomeId` is a temporary positive correlation ID owned by the current server Game until the associated outcome is durably stored or explicitly discarded. It is **not** the SQL outcome row ID.

### Database row identity

Physical SQL row IDs are storage implementation identities. Never substitute them for a Unity/server temporary `OutcomeId` merely because both are numeric.

### Diagnostic rule

For stale-state bugs, first ask:

1. Which identity namespace owns this state?
2. Which lifecycle does that identity belong to?
3. Is the publisher proving that the owner is still current?

This single discipline explains a large fraction of historical lifecycle/network/pathfinding bugs.

## 6. Persistent squads and runtime squads are different systems

`SavedSquad`, `FleetShip`, and `SquadShip` are persistent fleet/profile structures. `Squad` and `Ship` are pooled runtime battle objects constructed from them.

Important derived flags include `DoesBelongToSavedSquad` and `IsLoadedIntoLevel`. These reflect current runtime ownership and must not be treated as permanent serialized truth.

A ship death can therefore have two simultaneous meanings:

- a runtime pooled object is being torn down and returned;
- persistent fleet/squad statistics or survival state may need updating.

Do not solve pooling cleanup by destroying persistent identity, and do not preserve runtime references merely because the persistent ship still exists.

## 7. Pooling and lifecycle

Pooling is central to Bees performance and correctness.

The conceptual reuse boundary is:

`Pool acquire -> Setup/new logical lifetime -> active gameplay -> Kill/ClearData -> deferred release -> Pool return -> later acquire as a new logical lifetime`

A reused object must not inherit behaviorally relevant data from the previous life. Reset candidates include:

- IDs and owner references;
- timers;
- pathfinding request/lifecycle state;
- target/weapon/command references;
- collision/hit histories;
- derived collections;
- async publication tokens;
- visibility/range state;
- death/cleanup flags;
- temporary outcome mappings.

Kill/teardown paths should be idempotent where Unity callbacks can repeat. Deferred releases must drain exactly once.

A previous major testing effort specifically protects real pool reuse for all current projectile types, commands, Ships/Squads and repeated Level reset cycles.

## 8. Battle data flow

A useful simplified battle path is:

`Stage -> Level -> GameState -> persistent fleet/squad records -> runtime Squad/Ship -> Weapon -> projectile/command -> combat outcome -> persistent/runtime stats -> teardown/save`

Commands and targeting originate through Squad/Ship/Weapon behavior. Damage and terminal state flow back through `GameState`/`Level`, with persistent fleet consequences where applicable.

Many bugs that appear to be “combat bugs” are actually identity or lifecycle bugs: a delayed projectile, pooled target, stale range owner, reused Squad or old command may still be trying to publish state.

## 9. Combat attribution and delayed projectiles

A projectile can outlive the command that launched it. Damage therefore cannot simply be attributed to “whatever command is currently active on the firing ship.”

Combat attribution carries the originating command's temporary `OutcomeId` through delayed projectile behavior. Later damage can then update the correct stored command/shooting outcome.

`Ship.LogAttackingDamage` historically relies on the target still having positive health before logging/killing, which helps make repeated lethal callbacks idempotent for statistics and cleanup.

Do not add a second ad-hoc attribution identity when the originating outcome already owns the delayed effect.

## 10. Weapons, range and targeting

`RangeCollider` owns weapon-range enter/exit bookkeeping. Targeting and shooting depend on live range state plus strategy selection.

Key concepts:

- weapon-range ownership must remove entries symmetrically;
- targeting lists are enemy-aware rather than arbitrary physics overlap lists;
- reserved/incoming damage may influence targeting decisions;
- one collider/contact exiting must not erase a valid contribution from another observer/contact;
- reverse weapon-range cleanup is part of Ship teardown correctness.

Physics callbacks and multi-observer behavior often require PlayMode validation because source-level or EditMode tests cannot reproduce Unity lifecycle ordering precisely.

## 11. Map-object visibility has source ownership

Interactive map objects such as Fire Tanks can be visible through multiple observing weapon/range sources.

`MapObjectVisibilityTracker` exists because visibility is shared derived state. One observer exiting must not globally hide an object that another live observer still sees.

This is the same general ownership pattern used elsewhere in the project: aggregate state should disappear only when the last live owner releases its contribution.

## 12. Coordinate-space warning

Gameplay/pathfinding positions are commonly Level-local, while Unity 2D physics APIs operate in world coordinates.

For example:

- pathfinding obstacle sampling uses explicit world/Level conversion through pathfinding scope code;
- weapon line-of-fire checks use world-space transforms and `Physics2D` APIs.

Before changing geometry code, name the coordinate space at every boundary. A numerically plausible position in the wrong space can produce subtle pathfinding or combat errors.

## 13. Pathfinding architecture

Pathfinding is asynchronous and performance-sensitive.

The publication path is roughly:

`Ship movement request -> request ID + Ship lifecycle ID -> worker slot / Task.Run search -> completed-result queue -> main-thread ownership validation -> Ship path state`

Three identities matter when publishing a completed search:

- the Ship object reference;
- the numeric/current path request ID;
- the Ship's pooled-lifecycle ID/token.

A stale worker result must not overwrite a newer destination or a reused Ship merely because its old request ID happens to collide numerically.

## 14. Pathfinding invalidation is not Task cancellation

Invalidating a path request changes **publication ownership**. It does not magically stop an already-running `Task.Run` search.

This distinction is deliberate. The system must remain safe when:

- an old worker finishes after a new destination was requested;
- a Ship is cleared and reused while a previous search still runs;
- a tracked target moves while an earlier replan is active;
- a retry/backoff window exists;
- a queued replacement request supersedes older queued work;
- a Level tears down before worker completion.

Do not “fix” stale results solely by trying to cancel every worker. Correct ownership checks at publication are the primary safety boundary.

## 15. Static versus dynamic obstacles

Pathfinding has two conceptually different obstacle layers.

### Static base layer

Static obstacles are discovered/registered as the authoritative clearance layer. `Obstacle.ClearanceMappingCollider` participates in geometry sampling.

Destructible static obstacles can dirty this layer. When an obstacle is broken apart/removed, the static clearance representation—including worker copies—must be rebuilt consistently.

### Dynamic layer

Moving collision asteroids are applied as a dynamic avoidance overlay when the Level enables collision asteroids.

The dynamic snapshot is tied to `Stage.FixedUpdates`. Tests that move an asteroid without advancing that fixed-update counter may accidentally reuse the old dynamic layer and therefore fail to exercise production behavior.

### Qualification implication

Dense pathfinding tests need real production-shaped `Obstacle` objects/colliders and pool registration. Mutating private arrays directly is weaker evidence because it bypasses obstacle discovery/ingestion.

## 16. Clearance, corridors and egress

Path validity depends on ship size and clearance rather than only the ship's center point.

Important pathfinding semantics include:

- hard versus preferred clearance;
- diagonal corner blocking;
- deterministic A* tie breaking;
- narrow-corridor behavior that differs by ship footprint;
- static egress behavior when a ship begins inside or against obstacle geometry.

Campaign obstacle geometry can therefore be strategically different for Scouts versus Carriers/Factories/Barges even when their centers could theoretically follow the same geometric line.

## 17. Tracked-target movement

Aggressive/tracked-target movement should not continuously spam replans for insignificant target motion.

The ownership model keeps the currently active worker/retry window authoritative while allowing meaningful-distance/time changes to request a replacement. An older completion must still lose publication ownership if a newer movement request superseded it.

Pathfinding performance fixes must preserve this publication behavior; reducing worker count or replan frequency is not valid if ships begin following stale targets.

## 18. Obstacles are gameplay assets

Mission obstacle prefabs are not decorative backgrounds. `SpawnObstacles()` registers static obstacle geometry and embedded interactive `MapObject`s.

Changing an obstacle prefab can affect:

- path availability;
- ship-size clearance;
- cover/line of fire;
- visible interactive hazards;
- spawn geometry;
- campaign objective difficulty;
- dynamic route opening after destruction.

Qualification for campaign missions should load the exact authored prefab when obstacle semantics matter.

## 19. Fire Tank gameplay model

Fire Tanks are interactive neutral/destructible map objects tied to obstacles.

Core flow:

`CanisterBomb damage -> lethal Kill -> linked Obstacle.BreakApart(...) -> Obstacle.Kill() -> pathfinder static layer dirty/rebuild`

A Fire Tank has historically been authored at 250 health with detonation power 350. Its explosion is allowed to damage ships regardless of side, so controlled demolition can harm friendly ships standing too close.

The important gameplay consequence is not simply “exploding barrel.” A Fire Tank can open a route by removing a linked obstacle while simultaneously creating a local positioning hazard.

## 20. Fire Tank damage visuals

`MapObject.Setup` calls an overridable `InitializeSprite`, and damage handling calls `OnHealthChanged`. Fire Tank damage visuals correctly hook into those extension points rather than duplicating generic projectile-hit logic.

The body sprites are grouped as four damage stages per color variant. Damage stages advance around 25%, 50% and 75% health lost.

Serialized Fire Tank sprite references require the texture GUID **and each sliced sprite's internal file ID** from the texture `.meta` `nameFileIdTable`. Do not substitute the texture's main file ID.

Smoke is a lightweight sprite-frame animation driven directly by the Fire Tank rather than a separate Animator Controller.

## 21. Obstacle destruction debris

Fire Tank obstacle debris is cosmetic and deliberately isolated from gameplay physics/pathfinding.

Important implementation lessons:

- breakup geometry belongs on `Obstacle`; callers supply explosion origin/sprite/tuning;
- `_breakApartStarted` is a distinct guard and resets on reuse;
- debris has no collider and no `Rigidbody2D`;
- debris roots move/rotate visually and fade out;
- sub-sprite pivots are compensated with a centered renderer child rather than rewriting all source sprite pivots;
- spawn positions come from destroyed obstacle collider bounds when available;
- cosmetic randomness uses a local seeded `System.Random`, not global `UnityEngine.Random`, so visuals do not perturb deterministic simulation/replay state;
- instantiate/destroy is acceptable for currently infrequent Fire Tank explosions; pooling would be justified only if profiling shows meaningful GC/allocation cost.

## 22. Campaign philosophy

The campaign is a persistent attrition/resource war, not a set of disconnected RTS skirmishes.

Consequences can persist across missions:

- human ship losses;
- Bee losses;
- surviving Bee veterans;
- mined resources;
- Factory survival;
- retreat choices;
- branching/skipped content;
- unlocked ship/content progression.

A campaign test or refactor that resets every mission into an isolated “full fleet versus full fleet” state would miss the strategic design.

## 23. Campaign identity is multi-source

Mission truth is the combination of:

`CampaignMissionCatalog + LevelIntro + current runtime/server level data + trigger/objective logic + exact map + exact obstacle prefab + ship mechanics + spawn geometry + dialogue/UI + persistence consequences`

Do **not** treat the following as sole authorities:

- `SaveData/` snapshots;
- older `Scripts/Data/campaign_levels.json` alone;
- one trigger method;
- one scene or prefab;
- an old design document.

`SaveData/` is local/testing state and can be deleted. It is not game design.

## 24. Current scripted campaign arc

Current maintained mission ordering is:

0. **Anomaly**
1. **Reinforcements**
2. **Pushback**
3. **Bluer Pastures**
4. **Seize the Means**
5. **Of Production**
6. **Pressing Forward**
7. **Minesweeper**
8. **Beenoculars**
9. **On the Offensive**
10. **On the Defensive**
11. **A New Threat**

Missions 0-6 are the completed persisted scenario-ready set used by the automated campaign scenario harness.

Titania missions 7-8 remain explicitly `InDevelopment` in the maintained test/campaign contract.

Uranus missions 9-11 have current trigger behavior but lack the same current persisted campaign-record support, so automated campaign qualification deliberately excludes them.

More campaign levels are planned. Mission 11 currently acts like a broad-unlock endpoint because it is the present end of scripted content, not because it is intended as the permanent final campaign conclusion.

## 25. Pluto arc

### Mission 0 — Anomaly

Scripted exploration/discovery introduces the setting, a first Honeybee pursuit/combat and evidence of a larger invasion.

### Mission 1 — Reinforcements

Introduces fleet control followed by a relatively small elimination fight.

### Mission 2 — Pushback

First more straightforward fleet-elimination battle.

### Mission 3 — Bluer Pastures / Pluto IV

A timed Pluto defense/evacuation. Casualties and evacuation performance affect future fleet strength, explicitly teaching persistent attrition.

Pluto IV also has the Planetary Shield/timer/Evacuated HUD cluster discussed in the responsive-UI section below.

## 26. Neptune resource-war arc

### Mission 4 — Seize the Means

Dispersed assault around mining sites. Winning unlocks Factories. Losing can allow Bee-mined minerals to become future Bee strength and can skip the human mining mission.

### Mission 5 — Of Production

Escalating extraction/endurance. Factories mine resources while the player chooses how long to remain under increasingly dangerous Bee waves. Retreat can preserve assets; greed can destroy them.

### Mission 6 — Pressing Forward

Blockade-breaking elimination battle. Failure can cost remaining human Factories. The transition moves toward Carrier-era gameplay.

## 27. Titania: Minesweeper

Minesweeper is fundamentally a **route-selection and controlled-demolition** mission, not just a generic avoidance fight.

The authored `Resources/Obstacles/Minesweeper.prefab` has historically contained 45 obstacle instances and 30 Fire Tanks. The Fire Tanks carry `TargetObstacle` links, allowing some barriers to be removed.

Therefore the tactical loop is:

1. discover/understand the explosive obstacle;
2. decide which barrier to open;
3. fire from a safe enough location;
4. survive the neutral explosion hazard;
5. use the newly opened lane.

Not every authored obstacle is necessarily tank-removable. Route topology should be inferred from actual prefab geometry and links, not raw object counts.

The intended onboarding also includes dialogue on discovering the first map object and follow-up dialogue after destroying the first explosive. Preserve that teaching function when Titania content is completed.

## 28. Titania: Beenoculars

Beenoculars is a prolonged multidirectional defense centered on an immobile Titania objective.

Its authored obstacle prefab defines lanes/cover and therefore affects encounter behavior. It is not interchangeable with an empty arena.

The mission remains in-development in the current automated campaign readiness model, so do not fabricate persistence/readiness simply because trigger logic exists.

## 29. Uranus escalation

The current late-game scripted sequence is:

- **On the Offensive** — elimination offensive introducing/teaching later ships such as Carrier Strikers, Cruisers and Bumblebees;
- **On the Defensive** — harder mining/extraction siege with retreat pressure and large two-direction waves;
- **A New Threat** — Barge rescue/protection battle with reinforcement pressure and Barge-state dialogue.

Because these missions are not covered by the same persisted campaign records as 0-6, current automated scenario qualification does not imply production readiness for this arc.

## 30. Important ship-role gameplay learned during campaign work

### Factories

Mining is spatial. A mining-capable ship moves to a `MiningAsteroid`, physically contacts its collider and extracts on a recurring timer. Mining contributes TSV and persistent `MineralsMinedThisLevel`.

Factories are very large ships, so mining missions are simultaneously escort and path-access problems.

### Yellow Jackets

Yellow Jackets are expendable contact bombers. Their bombing run culminates in collision/detonation, damaging the target and killing the Yellow Jacket. Their tactical pressure is interception rather than ordinary ranged DPS.

### Barges

Barges are huge charge/ram combat ships rather than passive transports. They wind up, charge rapidly, damage the contacted enemy, take significant reflected/self damage, then stop/cool down.

`A New Threat` therefore combines rescue/protection with management of a valuable offensive unit that can expose itself during a charge.

## 31. Ship clearance matters to campaign geometry

Approximate footprint classes vary substantially—from small Scouts/Hornets/Honeybees, through Frigates/Gunships/Wasps and medium cruisers, to large Carriers/Bumblebees and huge Factories/Barges.

A corridor that is traversable for a Scout may be inaccessible or tactically poor for a Factory or Carrier.

Do not validate authored mission geometry using only one ship size when the mission expects mixed fleets.

## 32. Persistence/profile flow

High-level profile flow:

`ConfigData -> UserData/DataFile -> local storage and/or Socket -> BeesServer stored_user_data`

Normal server-backed startup distinguishes:

- missing profile data;
- failed profile reads;
- malformed profile content;
- valid current-version data.

Those states must not be collapsed into one generic “load failed” path.

Malformed profile documents can be rebuilt from current defaults rather than partially applying corrupt state.

## 33. Atomic campaign/profile checkpoint

Seven related profile documents can be serialized into one reserved `__campaign_checkpoint__` payload through `CampaignCheckpoint`.

BeesServer commits that checkpoint as one transaction, creating a cross-repository atomic profile boundary.

This exists because a campaign save may span multiple logically related documents. A partial write can otherwise create impossible fleet/campaign state.

Ordinary individual-file writes still exist and remain valid for their intended paths; do not force every persistence operation through the campaign checkpoint.

## 34. Versioned server settings contract

`ConfigData.Version` participates directly in the BeesServer settings lookup contract.

Unity asks for settings such as:

- `configuration`;
- `starting-settings`;
- `ship-stats`.

The server lookup uses exact user/name/version semantics rather than a safe “nearest available version” fallback.

Changing client version therefore requires compatible server rows whose serialized shape the current Unity parser can consume.

Settings blobs are Unity-authored JSON-like content and may contain syntax such as comments/trailing commas. Server-side strict JSON parsing is not a substitute for Unity parser compatibility validation.

## 35. Map/prefab naming is code

Runtime map lookup relies on deliberate alignment between configuration/map-location names and prefab names.

A historical class of failures occurred when a runtime map location existed but the prefab normalization lookup could not find the matching map prefab.

When adding/renaming a map:

- inspect `ConfigData.Maps`/location semantics;
- inspect prefab names and conversion/normalization code;
- inspect scene/Resources references;
- preserve `.meta` GUID identity where existing assets are referenced;
- validate the actual mission/scene path that loads the map.

Do not casually regenerate Unity `.meta` files during remote edits.

## 36. Hive Mind is not ML-Agents

Current Hive Mind behavior is a cross-repository Unity/BeesServer system.

The mostly commented historical ML-Agents implementation in `Brain.cs` and `Training/trainer_config.yaml` is **not** the current production Hive Mind training path.

This distinction is important because repository search can easily surface the dormant Agent callbacks and cause a new model to reason about the wrong system.

If ML-Agents is ever revived, it should be treated as an explicit separate experiment rather than silently inferred from the current Hive Mind name.

## 37. Hive Mind high-level behavior

Hive Mind decisions are selected from hand-authored strategy families rather than being an end-to-end neural policy inside this Unity repository.

The major decision families are:

- targeting;
- strategic/command selection;
- shooting.

Unity constructs matchup/state information and requests strategies from BeesServer. The selected strategy then drives real runtime behavior. Outcome evidence is sent back so BeesServer can update learning history.

Because learning keys and strategy IDs persist, changing matchup construction or numeric strategy identity is a schema/evidence change, not a harmless refactor.

## 38. Hive Mind request/learning flow

A useful flow is:

`Squad matchup construction -> MatchupStrategyRequest/CommandRequest -> Socket standing request -> shared server Game -> strategy response + OutcomeId -> real runtime behavior -> command/shooting TSV attribution -> StoreCommands -> durable server learning history`

Matchup construction can include acting/enemy/ally composition and the available strategy set. Banned strategies affect strategic cache availability, so “same units” is not always the same strategic learning key if the available strategy set differs.

## 39. Hive Mind matchup and strategy identities

BeesServer produces deterministic unsigned xxHash64 matchup IDs and preserves them as decimal strings through Node/MySQL `BIGINT UNSIGNED`.

Unity response DTOs also retain matchup IDs as strings. Reintroducing signed `long` conversion can corrupt IDs above the signed range.

Strategy IDs are persistent learning schema. Current maintained server/client knowledge records command strategy IDs 1-15 and targeting/shooting IDs 1-40. Do not reorder/reuse numeric IDs in a way that reinterprets historical data; append stable IDs or migrate deliberately.

## 40. Hive Mind OutcomeId transaction

The OutcomeId returned to Unity is a temporary server-Game correlation identity.

A completed command can produce multiple learning outcomes—for example strategic command, targeting and shooting evidence. Unity later returns the associated outcome ID with measured TSV/evidence through `StoreCommands`.

Only then should the server persist the durable learning record.

Reconnect must preserve the appropriate server Game ownership long enough that pending outcome IDs remain meaningful across transient connection loss.

## 41. Shared server Game versus Unity Level

One Unity WebSocket can host multiple concurrent Levels that share one server-side Hive Mind `Game`.

This does **not** mean Unity should merge their lifecycle ownership.

The correct split is:

- backend `Game` can be connection-shared;
- Unity `Level` remains the client mutation/lifecycle owner;
- responses must still prove the target Level/request/Squad lifetime is current before publishing.

A response aimed at a dead/recycled Squad must be rejected even if it arrived from the correct connection Game.

## 42. Socket response lifecycle

Important response guards include request hash/type, owning Level and current pooled `Squad.ItemId`.

`SocketResponseLifecycleGuard` / standing-request logic exists to prevent late responses from mutating:

- an ended Level;
- a dead Squad;
- a recycled Squad with a new `ItemId`;
- an already-handled request.

Status handling also has semantic meaning. Authentication refresh/retry and terminal response statuses should not create infinite resend loops or duplicate command storage.

Handled-request history must remain bounded/pruned so reconnect reliability does not become an unbounded memory structure.

## 43. Reconnect ownership

Reconnect/setup responses must update the Level that owns the original request rather than an unrelated cached/current Level.

Historical bugs included cleanup that failed to mutate the shared handled-request set and reconnect responses assigning state through an unrelated setup-level field.

Focused tests now protect owner-specific reconnect mutation and request-hash cleanup.

## 44. Dedicated Hive Mind training

The `Hivemind Training` Unity scene serves more than one purpose. The scene name alone does not mean Hive Mind training is active.

When the mode is active training rather than Fish Tank:

`Hivemind Training scene -> HiveMindTrainingBootstrap -> Stage training flags -> multiple randomized non-rendered Levels -> ordinary real Hive Mind request/learning path`

Current maintained architecture describes 16 non-rendered randomized Levels using the same production Hive Mind learning route.

This is intentionally different from the dormant ML-Agents `Brain` path.

## 45. Fish Tank distinction

The same scene can also be used as player-facing Fish Tank behavior.

Therefore:

- scene identity is not enough to infer training;
- `Stage.IsTrainingHiveMind`/mode state matters;
- code that optimizes or disables rendering for training must not accidentally change Fish Tank semantics.

## 46. Replay and determinism

Live replay input kinds currently include:

- `user-command`;
- `user-move`;
- `hivemind-matchup-response`;
- `hivemind-command-response`.

Replay snapshots/checkpoints must use stable ordering. Squads and ships should be sorted by stable IDs before serialization rather than relying on `HashSet`/dictionary enumeration.

`SimulationReplayRandomScope` separates/reproduces random streams so deterministic gameplay evidence is not perturbed by unrelated global randomness.

Purely cosmetic systems, such as obstacle debris, should use isolated local randomness for the same reason.

## 47. Screen-space UI architecture

Responsive UI is not owned by one generic script. There are layered ownership boundaries.

The high-level sequence is:

`CanvasScaler/root canvas -> ResponsiveScreenLayoutGuard -> semantic screen/game-specific guards -> RootCanvasCompatibilityGuard`

A second structural pass, `LegacyScreenResponsiveLayoutGuard`, exists for specific large legacy layouts such as Main Menu and Squad Maker.

The central lesson from repeated UI regressions is: **geometry ownership must match semantic ownership**.

A generic guard should not “fix” an arbitrary nested control whose intended relationship it does not understand.

## 48. `ResponsiveScreenLayoutGuard`

This guard owns legacy viewport/screen-wrapper normalization.

It can repair screen-relative wrappers that remain tied to the old 1366x768 reference rectangle, including late-created root canvases.

It must not:

- stretch ordinary centered panels merely because they are large;
- rewrite children owned by a `LayoutGroup` independently;
- translate arbitrary nested HUD islands;
- treat every full-stretch child as a viewport owner.

The historic 1366x768 authoring size is a reference, not a required runtime viewport.

## 49. `RootCanvasCompatibilityGuard`

This guard performs a final screen ownership-boundary correction.

It repairs true viewport owners, not intentional inset panels.

A full-stretch child is only a viewport candidate when it effectively fills its parent. A full-stretch Main Menu panel with deliberate authored offsets must retain those offsets.

The guard can also:

- give taller-screen surplus to a dominant fixed-height body when a fixed footer/tool row must stay at the real bottom;
- clamp whole direct root-canvas interactive islands that are genuinely outside the canvas;
- give explicit BACK/CONTINUE/SKIP-style screen-edge navigation controls a small rendering inset.

It does **not** position gameplay Squad Tabs.

## 50. `GameHudLayoutGuard`

Gameplay HUD semantics are explicit:

- scoreboard flush to intended top edge/corner;
- ordinary Game Speed flush to its intended top corner;
- selected-squad action box and minimap flush to bottom corners;
- blank/whitespace mission-objective panels hidden;
- Squad Tabs begin immediately after the live scoreboard, or at the top-left canvas edge when no scoreboard is active;
- tabs wrap into additional rows before colliding with a visible mission-objective panel.

A stale serialized `GameMenus.Scoreboard` reference in the `Space` scene has historically pointed at the Summary panel, so the guard verifies object identity/name and resolves the real live scoreboard when necessary.

Sibling HUD geometry is converted through world space; do not assume scoreboard, mission panel and tabs share one transform parent.

## 51. Pluto IV and Titania II shared timed HUD

Pluto IV and Titania II both use the Planetary Shield and mission clock.

Pluto IV additionally displays an **Evacuated** counter. Titania II intentionally hides it.

Therefore a shield/timer/Game Speed fix must validate both missions.

The shared shield fill uses the established `0..150` convention rather than a generic `0..1` RectTransform interpretation.

Pluto IV Game Speed placement is derived from live shield/Evacuated geometry rather than a fixed absolute x offset. Titania II has its own right-aligned clock-relative placement.

When the timed mission state ends, ordinary authored Game Speed placement must be restored.

## 52. Main Menu responsive lessons

Main Menu has historically exposed two different failure modes:

- controls remained inside a legacy aspect-sized interactive frame, producing unused bands on wider/taller displays;
- an over-broad root compatibility pass expanded an intentionally inset panel and destroyed authored spacing.

The current solution is ownership-aware:

- expand the correct screen-scale interactive branch where needed;
- preserve deliberate inset offsets;
- do not use one generic “stretch everything” rule.

## 53. Squad Maker responsive lessons

Squad Maker is a particularly high-coupling scene/controller and has exposed several layout issues on non-reference screens.

`LegacyScreenResponsiveLayoutGuard` handles large structural nested layouts while filtering out small local button rows.

A later regression involved START and TEST hover descriptions. Both controls can be present simultaneously. Their hidden description objects were mistakenly allowed to participate in the right-column `LayoutGroup`, consuming space that should show campaign level title/details.

The durable fix is semantic: hover descriptions remain active as visual overlays but use `LayoutElement.ignoreLayout = true`, so hover UI does not reserve structural layout space.

## 54. Current UI regression lessons

Permanent regression coverage currently records at least these classes:

1. legacy screen-space UI tied to the 1366x768 authoring rectangle;
2. use of an unavailable Unity Canvas API during a responsive fix;
3. conflation of navigation margins, flush gameplay HUD edges, scoreboard/tab/objective relationships and inset-panel ownership;
4. Pluto IV/Titania II shield/timer/Game Speed drift;
5. nested legacy menu/Squad Maker layouts leaving gutters;
6. Squad Maker hover descriptions displacing campaign level details.

A new UI change should assume these are independent contracts, not one solved “responsive UI problem.”

## 55. Unity API/version compatibility

The maintained testing contract uses Unity `6000.5.4f1` and Unity Test Framework `1.7.0`.

A prior responsive fix referenced a Canvas API unavailable in the repository's Unity version and therefore failed compilation despite plausible source logic.

New Unity API usage must be verified against the actual project Unity version. Compilation/import is an essential part of validation.

## 56. Testing architecture

Production scripts still compile into Unity's predefined `Assembly-CSharp`.

Test assemblies cannot directly reference `Assembly-CSharp`, so existing EditMode/PlayMode tests often use `Tests/*/RuntimeAssembly.cs` reflection adapters.

Do not move the whole production project behind asmdefs solely to make one test easier. New pure/testable collaborators can use cleaner boundaries when independently justified.

Use:

- **EditMode** for deterministic contracts/state/pure geometry and real-prefab checks that do not need rendered frames;
- **PlayMode** for scene bootstrap, Unity destroyed-object behavior, actual background workers, physics callbacks, frame progression, rendering/GPU behavior and soak/lifecycle paths.

## 57. Unity command-line validation semantics

Unity process exit/log text alone is not the authoritative test result.

Use:

- Unity Test Framework XML;
- exact executed-test count;
- appropriate category.

Do not add `-quit` to the documented test command merely out of habit; the repository's Test Framework behavior can exit itself, and explicit `-quit` has historically terminated startup before tests executed.

## 58. Test categories and release gate

Important categories include:

- `BeesFoundation` EditMode;
- `BeesPlayModeFoundation`;
- `BeesCampaignScenario`;
- `BeesCampaignScene`;
- `BeesPerformanceQualification`;
- `BeesSoakQualification`.

The full local release gate is `Tools/Run-BeesReleaseGate.ps1`.

It can run Unity correctness/qualification categories and the external BeesServer Node tests. Options allow skipping qualification or server portions when deliberately unavailable.

Any test counts recorded in `docs/TESTING.md` are snapshot evidence only. Re-run them on the exact changed source before claiming current validation.

## 59. Campaign test boundary

The deterministic campaign harness protects completed persisted missions 0-6.

`CampaignScenarioIsolation` must be active **before** loading the real `Space` scene so networking/audio persistent bootstrap can be suppressed.

`CampaignScenarioSceneHost` can load the real scene additively, preserve real Stage/prefab/pool references and create an isolated Level shell.

It intentionally does not run normal user-data/fleet bootstrap.

Running full mission setup unsafely can resolve persistent FleetShips through global `ConfigData.CurrentShips` and touch real player data. Do not call campaign setup from an isolated test until its fleet/ship-stat/UI/dialogue/persistence dependencies are explicitly supplied or replaced.

## 60. Existing test protection worth knowing

The maintained foundation/PlayMode coverage protects many previously fragile contracts, including:

- timer reuse/cancellation;
- `GameState.ResetState` clearing indexes/flags/visibility/outcomes;
- Ship/Squad registry symmetry;
- persistent loaded-into-level flags;
- deferred release exactly once;
- pool reuse across projectile/command types;
- lazy socket initialization;
- owner-correct reconnect;
- async path request and pooled-lifecycle ownership;
- real-worker stale-result rejection;
- response hash/type/Level/Squad-lifetime guards;
- duplicate outcome ID rejection and stored-command mapping ownership;
- persistence golden cases and malformed-write atomicity;
- campaign mission catalog identity/terminal behavior;
- combat health/TSV/statistics/range/cleanup behavior;
- friendly-fire eligibility matrices;
- replay ordering/random-scope contracts;
- responsive layout geometry regressions.

A green focused test proves only its exercised contract; it is not a whole-game certification.

## 61. Performance objective

The performance target is broad low-end compatibility with high/stable frame rate and bounded CPU/GPU/RAM/GC/resource use.

Optimization should prefer removing real work or allocation from hot paths rather than silently reducing quality or correctness.

Important hot surfaces include:

- per-frame/per-fixed-step loops;
- pathfinding and obstacle snapshots;
- physics/range/visibility work;
- pooling/lifecycle;
- UI/layout work;
- rendering;
- network serialization/copying;
- repeated state scans;
- checkpoint/save I/O.

The current `PERFORMANCE_LEDGER.md` has no unresolved validated opportunities after its static optimization passes. That does **not** mean the game is fully optimized; it means no additional opportunity met the ledger's validation threshold at the snapshot.

## 62. Performance qualification is not minimum-spec certification

`BeesPerformanceQualification` is a CPU regression baseline, historically including:

- one path worker;
- 64x64 open path grid;
- 25 real background searches;
- 10,000 real `GameState.ResetState` calls.

It is useful for regressions but does not prove acceptable rendered gameplay on the minimum physical PC.

Hardware qualification should record CPU/core count, RAM, GPU/VRAM/API, display resolution, OS, Unity version and batch/headless status.

Long soak work should watch stable pool/runtime baselines and monotonic memory growth, not only one absolute managed-memory threshold.

## 63. BeesServer database boundary

The Unity repository contains enough cross-repository knowledge to avoid obvious protocol mistakes, but server internals remain authoritative in `allStackGit/BeesServer`.

Maintained client-side knowledge includes:

- normal server operation uses the production database while integration/test mode must use the isolated test database;
- settings/version lookup is exact;
- `stored_user_data` is opaque profile JSON keyed logically by user/filename;
- shared campaign/challenge level data uses reserved/shared ownership semantics;
- Hive Mind matchup and strategy identities are persistent schema;
- temporary OutcomeIds must not be confused with SQL row IDs;
- reconnect must preserve pending learning ownership correctly;
- atomic campaign checkpoints are cross-repository transactions.

Do not carry an old BeesServer audit forward as current truth. Read the current server `AGENTS.md`, context index and implementation before server changes.

## 64. Current bug/quality/performance status

At this snapshot:

- `BUG_LEDGER.md` records no remaining validated defects from its last static audit;
- `PERFORMANCE_LEDGER.md` records no unresolved validated optimization opportunities;
- `QUALITY_LEDGER.md` still records bounded maintainability debt.

“Ledger empty” means no currently recorded validated item under that ledger's methodology. It does not mean the repository is defect-free or fully optimized.

## 65. Important maintainability debt

### Tracked IDE workspace state

`Scripts/.idea/` is tracked and adds machine-specific/search noise. Cleanup is deferred to a focused repository-layout change.

### Dormant ML-Agents `Brain`

The large commented historical ML-Agents code is easy for agents to mistake for current Hive Mind logic. Long-term cleanup/isolation would reduce confusion but must preserve serialized references and historical intent safely.

### `SquadMaker` controller size/coupling

`SquadMaker.cs` coordinates a large serialized UI surface plus persistent fleet editing, economy/build flow, drag/drop, level selection/options, validation/dialogues and scene transition. Continued decomposition behind the existing serialized facade would lower context and regression risk.

### `Utilities` hub

`Utilities.cs` combines protocol/persistence-sensitive identity maps with unrelated helpers. Schema-like ship/command/strategy conversions should eventually be isolated behind compatibility-preserving APIs and direct tests.

## 66. Open learning candidate

One unresolved repository-learning item at this snapshot is the root-level `Squad Maker Aspect Ratio.unity` scene.

It is large, outside the normal `Scenes/` hierarchy and has no obvious exact-name source reference. It may be intentional responsive-layout evidence or obsolete scratch material.

Do not delete or treat it as authoritative until Unity Build Settings/reference inspection and comparison with the canonical Squad Maker scene establish its role.

## 67. Known validation gaps

The maintained testing guide still identifies gaps such as:

- true scene-capable end-to-end campaign objective/playthrough driving;
- additional deterministic simultaneous-kill and representative many-ship combat scenarios;
- event-specific replay playback adapters and deterministic Unity physics/state snapshots;
- named minimum-spec hardware certification, dense battle/rendered UI/GPU/memory and 30–60 minute soak evidence;
- deeper isolated server DB/WebSocket/reconnect/concurrency/failure-recovery integration.

Do not interpret strong unit/lifecycle coverage as proof these gaps have been closed.

## 68. Common wrong assumptions for a new model

Do not assume:

- every integer ID is interchangeable;
- invalidating a path request cancels the already-running worker;
- a recycled Unity object is the same logical Ship/Squad lifetime;
- current scene or socket implies ownership of every Level;
- a map/obstacle prefab is decorative;
- `SaveData/` is authoritative campaign design;
- trigger code alone defines a mission;
- full-stretch RectTransform anchors mean “expand this panel to the screen”;
- desktop gameplay HUD needs generic safe-area padding;
- Pluto IV and Titania II have independent shield/timer UI;
- START and TEST are mutually exclusive in Squad Maker;
- the historical ML-Agents `Brain` is current Hive Mind training;
- a Hive Mind OutcomeId is a database row ID;
- a signed C# `long` is safe for every unsigned matchup hash;
- one WebSocket implies one Unity Level or one backend lifecycle owner;
- a passing focused Unity test proves the entire game;
- an empty bug/performance ledger means there are no unknown problems;
- an old XML/log validates source changed afterward;
- Unity engine APIs can be selected from memory without checking the repository's actual Unity version.

## 69. Fast cold-start domain map

| Domain | Start with |
|---|---|
| normal task routing | `docs/engineering/CONTEXT_INDEX.md` |
| stable product rules | `PROJECT_CONSTITUTION.md` |
| runtime ownership and identities | `docs/engineering/SYSTEM_MAP.md` |
| must-preserve cross-cutting rules | `docs/engineering/INVARIANTS.md` |
| detailed accumulated implementation knowledge | `docs/DEVELOPMENT_MEMORY.md` |
| testing commands/contracts | `docs/TESTING.md` |
| fixed regressions | `docs/engineering/REGRESSIONS.md` |
| unresolved learning | `docs/engineering/LEARNING_STATE.md` |
| current validated bug queue | `BUG_LEDGER.md` |
| maintainability debt | `QUALITY_LEDGER.md` |
| performance queue/status | `PERFORMANCE_LEDGER.md` |
| Level/GameState lifecycle | `Level`, `GameState`, Pool/Setup/ClearData/Kill paths |
| persistent fleet/squads | `Ships`, `FleetShip`, `SavedSquad`, `SquadShip`, `LevelConstructor` |
| pathfinding | `Pathfinder`, `Pathfinder.Search`, obstacle layers, Ship movement/lifecycle |
| combat/targeting | `Ship.Combat`, `Weapon`, `RangeCollider`, command/outcome attribution |
| Hive Mind client | `Squad.Commands`, matchup construction, `Socket`, request/response DTOs |
| Hive Mind server | switch to `allStackGit/BeesServer` and read its current `AGENTS.md`/context index |
| profile persistence | `DataFile`, `UserData`, `CampaignCheckpoint`, socket storage calls |
| campaign | `CampaignMissionCatalog`, `LevelIntro`, Level triggers, runtime data, exact maps/obstacles |
| responsive UI | responsive/root/HUD/legacy/SquadMaker layout guards + regression ledger |
| Fire Tank | `CanisterBomb`, linked `Obstacle`, Fire Tank prefab/sprites |
| replay | replay recorder/player, stable ID sorting, random scopes |

## 70. How to approach a bug safely

For a focused defect:

1. Read current `AGENTS.md`.
2. Find the concept in `CONTEXT_INDEX.md`.
3. Name the enduring contract and owner/lifetime.
4. Read only the linked owner-doc section and current source/assets/tests needed to resolve it.
5. If async, identify both work ownership and publication ownership.
6. If identity-related, name the exact namespace.
7. If Unity-serialized, inspect prefab/scene/Resources/.meta implications.
8. If cross-repository, inspect both current client and server implementations.
9. Classify affected tests before changing them.
10. Add focused regression coverage when practical.
11. Widen validation proportionally to the risk.

Stop reading once the contract, symbols, ownership boundary and required evidence are clear. The normal workflow is deliberately context-efficient.

## 71. Maintaining this primer

This file should not become a chronological conversation dump.

Refresh it only during explicit onboarding/curation work, or when broad project knowledge has changed enough that a cold-start model would receive a materially incorrect mental model.

When refreshing:

- verify against current source/assets/tests;
- replace stale statements instead of appending contradictions;
- distinguish stable requirements, current implementation, historical lessons and unresolved gaps;
- summarize detailed owner documents instead of duplicating every line;
- preserve explicit in-development content boundaries;
- do not include credentials, tokens, private server data or personal save data;
- do not copy a historical server finding into current status without checking BeesServer;
- keep routine learning in the smaller owner docs and context index.

The intended result is that a future model with no conversational memory can read this file once, understand the major systems and historical lessons, and then work through the repository's normal lightweight context-routing workflow rather than reconstructing years of project knowledge from scratch.
