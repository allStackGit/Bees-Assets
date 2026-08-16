# Bees Context Index

Compact routing map for agents. Search this before broad repository scans, then inspect linked current source/assets/tests. Detailed facts belong in owner documents; this index is navigation, not authority.

| Concept / useful aliases | Start with | Current code / assets / symbols to locate | Evidence / related concepts |
|---|---|---|---|
| bootstrap, global config, scene startup | `SYSTEM_MAP.md` → Runtime ownership; development memory | `ConfigData`, `Scene`, `Stage` | user-data finalization; socket bootstrap; prefabs |
| Level state, reset, teardown, pooling | development memory → testing/lifecycle; invariants → State and lifecycle | `Level`, `GameState`, `Pool`, `Setup`, `ClearData`, `Kill` | foundation/soak tests; deferred release |
| pathfinding, stale paths, obstacles | development memory → Pathfinding and performance qualification | `Pathfinder`, Ship path request state, `Obstacle`, clearance mapping | lifecycle tokens; worker ownership; performance qualification |
| combat, targeting, range, projectiles | development memory → Combat/targeting | `Squad`, `Ship`, `RangeCollider`, `Weapon`, `Projectile` | CombatLifecycleIntegrationTests; visibility ownership |
| campaign, mission IDs, triggers, progression | development memory → Campaign qualification/gameplay; system map → Campaign | `CampaignMissionCatalog`, `LevelIntro`, mission trigger/objective code | runtime level data; exact map/obstacle prefab; persistence/dialogue |
| maps, prefabs, Resources, normalization | invariants → Maps/prefabs/scenes/assets | map prefab normalization, `Resources/Obstacles`, pool/prefab lookup | map/config names; `.meta` GUIDs; campaign identity |
| Pluto IV, Titania II, shield clock, Game Speed | invariants → UI and display layout; regressions REG-004 | `GameHudLayoutGuard`, Pluto4/Titania2 mission code | shared shield/timer; Pluto Evacuated counter; UI tests |
| responsive UI, Mac, aspect ratio, white strip | regressions REG-001..REG-003; system map → Screen-space UI | `ResponsiveScreenLayoutGuard`, `GameHudLayoutGuard`, `RootCanvasCompatibilityGuard` | EditMode geometry + rendered PlayMode/platform checks |
| persistence, user data, server contract | development memory → BeesServer/database contract; system map → Persistence/network | `ConfigData`, `UserData`, `DataFile`, `Socket`, request/response DTOs | BeesServer cross-repo contract; versioning/reconnect |
| replay, determinism, ordering | development memory → Combat/targeting/replay; invariants → Async/order | replay recorder/player, stable ID sorting, random scopes | unordered collections; cosmetic random isolation |
| Fire Tank, canister, obstacle destruction | development memory → Fire Tank visuals / Obstacle debris | `CanisterBomb`, `Obstacle.BreakApart`, Fire Tank prefab | pathfinder dirtying; neutral hazard; debris determinism |
| performance, low-end, frame time, GC | development memory → Pathfinding/performance qualification; performance skill | Update/FixedUpdate hot paths, pools, pathfinding, physics/UI/rendering | `BeesPerformanceQualification`; soak; named hardware |
| tests, Unity runner, release gate | `docs/TESTING.md`; validation policy | `Tests/EditMode`, `Tests/PlayMode`, `Tools/Run-BeesReleaseGate.ps1` | XML + executed count; PlayMode for frame/physics/scene |
| agent learning, context, retrieval | this file; `LEARNING_STATE.md`; repo-learning skill | `.agents/skills/{repo-learning,continuous-learning,search-index,code-quality}` | repeated misses; quality ledger; EngineeringGuardrailTests |

## Retrieval rules

- Start with the matching row and named document section; do not automatically read all of `docs/DEVELOPMENT_MEMORY.md`.
- Search exact symbol, asset, scene, prefab or error terms before broad scans.
- When one concept repeatedly requires another, add the relationship here rather than copying implementation detail.
- Update stale routes when touched code/assets move. Material behavior must still be verified from current source/assets/tests.
