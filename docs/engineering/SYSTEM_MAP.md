# Bees Engineering System Map

Concise, maintained orientation for coding work. This file is intentionally smaller and more current than `docs/BEES_SYSTEM_DESIGN.md`. The older design review remains useful historical/source-analysis context, but dated claims in it must be revalidated; for example, its original statement that the project had no first-party tests is no longer current.

## Runtime ownership

- `ConfigData` — global configuration, enums, selected mode/data handles, settings, user-data bootstrap, and lazy socket access. Changes have wide reach.
- `Scene` / `Stage` — scene lifecycle, network/data-readiness pump, shared pools/prefabs/UI/input/audio/camera, and one or more Levels.
- `Level` — one battle arena: options, map/environment, timers, server game context, objectives, teardown, saving, and the owning `GameState`.
- `GameState` — authoritative per-Level runtime registries, selections/visibility, commands/outcomes, dynamic state, score/counters, request ownership, and deferred releases.
- `SavedSquad` / `FleetShip` / `Squad` / `Ship` — persistent-to-runtime fleet construction and combat lifecycle.
- `Pool` plus object `Setup`/`ClearData`/`Kill` paths — object reuse boundary. Reacquired objects must behave as new lifetimes.
- `Pathfinder` plus `Ship` path request state — background path search and main-thread publication. Request identity and pooled-lifecycle identity both matter.
- `RangeCollider` / `Weapon` / `Projectile` — targeting-range ownership, firing, collision, damage, and cleanup.
- campaign mission catalog/intro/trigger/objective code — campaign identity and terminal behavior; serialized map/obstacle assets and persistence data are part of the same contract.
- `Socket` and request/response DTOs — server transport, resend/deduplication/reconnect, and response dispatch into the owning runtime objects.

## Important data flows

### Battle

`Scene -> Stage -> Level -> GameState -> SavedSquad/FleetShip -> Squad/Ship`

Commands and targeting flow through Squad/Ship/Weapon/Projectile, while terminal state flows back through GameState/Level into persistence and cleanup.

### Pathfinding

`Ship request -> Pathfinder queue/worker -> completion -> ownership checks -> current Ship path state`

Any optimization/refactor must preserve newest-request ownership and pooled-lifecycle isolation.

### Persistence/network

`ConfigData/UserData/DataFile <-> local storage and/or Socket <-> BeesServer`

Level/game/request identifiers and versioned payload shapes are ownership boundaries, not incidental metadata.

### Campaign

`CampaignMissionCatalog + LevelIntro + runtime level data + trigger/objective code + exact map/obstacle prefab + ship mechanics/spawn geometry + dialogue/UI + persistence consequences`

Do not use `SaveData/`, old JSON, trigger source, or an individual prefab as a sole source of mission truth.

### Screen-space UI

`CanvasScaler/root canvas -> ResponsiveScreenLayoutGuard viewport-wrapper repair -> GameHudLayoutGuard semantic gameplay placement -> RootCanvasCompatibilityGuard final ownership-boundary correction`

`ResponsiveScreenLayoutGuard` owns legacy viewport/screen-wrapper geometry and must not move arbitrary nested UI. `GameHudLayoutGuard` owns gameplay semantics: scoreboard and other edge controls receive a small visible inset, the Squad Tabs root is made screen-sized and its row begins from the live scoreboard right edge (or the top-left inset when the scoreboard is hidden), timed mission HUD placement remains mission-aware, and the action box/minimap stay visibly inside the bottom corners. `RootCanvasCompatibilityGuard` does not position Squad Tabs; it repairs viewport-level layout owners/backers, gives taller-display surplus to a dominant fixed-height body when a fixed footer/tool row must remain at the real bottom, and clamps only whole direct root-canvas interactive islands that are actually outside the canvas.

## Testing architecture

- EditMode tests and PlayMode tests live under `Tests/` and are described by `docs/TESTING.md`.
- Production gameplay still compiles in Unity's predefined `Assembly-CSharp`; test assemblies use `RuntimeAssembly` reflection adapters where direct references are unavailable.
- Prefer EditMode for deterministic state/contracts and PlayMode for scene/frame/physics/async Unity behavior.
- XML test results plus executed-test counts are authoritative for command-line Unity test runs.
- The external BeesServer repository has its own tests; Unity changes that alter its protocol still require cross-repository reasoning even though this repository cannot enforce the server suite by itself.

## High-risk change surfaces

Inspect broader dependencies before modifying:

- `ConfigData`, `Utilities`, `Level`, `GameState`, `Pool`, `Ship`, `Squad`, `Pathfinder`, `Socket`;
- campaign trigger/catalog/level-data and map/obstacle-prefab wiring;
- object pooling and cleanup;
- async/background work;
- reconnect/deduplication/persistence;
- scene bootstrap and user-data finalization;
- physics/range/visibility ownership;
- replay/deterministic evidence;
- performance-sensitive per-frame/per-fixed-step loops.

## Maintenance rule

This map is navigation, not proof. When a task reveals that ownership, call paths, or canonical sources changed, update this file and/or `docs/DEVELOPMENT_MEMORY.md` in the same task. Remove stale statements rather than leaving multiple contradictory maps.