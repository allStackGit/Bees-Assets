# Testing and architecture

## Unity/test boundary

- Production runtime scripts compile into Unity's predefined `Assembly-CSharp`. EditMode/PlayMode tests cannot reference that assembly directly, so existing tests intentionally use the `Tests/*/RuntimeAssembly.cs` reflection adapters.
- Prefer EditMode for deterministic state/lifecycle and real-prefab qualification that does not require rendered-frame progression. Use PlayMode for scene bootstrap, physics callbacks, destroyed-object semantics, async/frame progression, rendering/GPU behavior, and long-running soak work.
- Authoritative command-line validation is Unity Test Framework XML plus the exact executed-test count. Unity process exit/log noise alone is not sufficient evidence.
- The release gate is `Tools/Run-BeesReleaseGate.ps1`; slow qualification categories should stay opt-in so the normal correctness loop remains practical.
- Leave new Unity `.meta` generation to Unity during remote repository work.

## GitHub development workflow

- Treat recurring GitHub/connector friction as an engineering problem to eliminate, not a permanent limitation to work around manually. When repository structure or tooling makes a development operation risky, slow, or repetitive, first create a durable safer workflow, record it here, and use it for subsequent applicable work.
- **Oversized source files are never a reason to defer or skip a required fix.** When a file is too large for safe/reliable connector editing, split it into smaller cohesive partials/helpers first, preserve behavior, and then make the intended change in the smaller ownership file. File size is a refactoring trigger, not a blocker.
- In particular, do not repeatedly accept risky whole-file replacement of oversized source files. Split large components along cohesive ownership boundaries (usually partial classes for Unity components that share private state, or focused helpers for independent behavior) so future changes can be made through small files with reviewable diffs.
- Apply the decomposition rule immediately when size interferes with safe editing; do not leave a confirmed bug open merely because its current owner is a large file. This rule applies to `ConfigData`, `Level`, `Stage`, `Utilities`, `Ships`, and any future oversized component just as it does to already-split classes.
- When a new friction pattern is discovered, record both the cause and the adopted solution in repo memory before continuing. Prefer solutions that reduce future context, token use, connector payload size, and regression surface rather than one-off procedural workarounds.
- For an unavoidable one-time large-file edit, fetch the exact blob first and inspect the resulting commit diff immediately. If the diff contains unrelated formatting/comment churn, do not carry it forward: move the branch ref back to the clean parent, decompose the file structurally, and retry through the new smaller ownership files.
- `Pathfinder` is the reference example for this recovery pattern: a noisy whole-file attempt was discarded immediately, then the class was decomposed into core, obstacle-mapping, search/worker, and model partials plus a small coordinate/scope helper. Future Pathfinder changes should target those focused files rather than recreating the monolith.
- Existing examples of the broader decomposition rule are the `Ship`, `GameState`, `Turret`, campaign, and `Pathfinder` partial decompositions. Apply the same pattern proactively to remaining oversized files such as `Level`, `Stage`, `Utilities`, or `Ships` when their size materially interferes with safe GitHub development.

## Source organization

- Large Unity components should be split along ownership boundaries before they become unsafe to edit through whole-file tooling. Partial classes are appropriate when the behavior still belongs to one Unity component and must share private state.
- Keep lifecycle/reset code close to the state it owns; move independent targeting, geometry, persistence, campaign, or diagnostics behavior into focused partials/helpers.
- Do not perform broad formatting/comment cleanup as a side effect of a bug fix. Large textual rewrites make regression review substantially harder even when executable behavior is unchanged.
- When sorting/shuffling data returned from runtime owners, verify whether the getter exposes the authoritative mutable collection. Make a snapshot (`ToList`) before reordering shared state.
- `Ship` is intentionally decomposed into shared state plus `Lifecycle`, `Movement`, `Combat`, `Geometry`, `Visuals`, `Debug`, and `Interaction` partials. `GameState` is split into core/reset, registry/release, queries, selection, and command-history ownership. `Turret` is split into lifecycle, aiming/geometry, and targeting/firing. `Pathfinder` is split into core coordinate/result ownership, obstacle mapping, search/worker state, and grid/model types.
- `ConfigData` is intentionally decomposed: `ConfigData.cs` owns shared roots/state, `ConfigData.Types.cs` owns enums, `ConfigData.Gameplay.cs` owns gameplay constants/tables/formations, `ConfigData.Runtime.cs` owns data/bootstrap/runtime helpers, and `ConfigData.Campaign.cs` owns campaign scene routing. Do not regrow the former monolith; route future edits to the focused partial.
- Campaign trigger ownership is similarly decomposed: `Level.Campaign.Shared.cs` owns common trigger/target/reinforcement helpers, `Level.Campaign.Endings.cs` owns legacy Pluto/Neptune/Uranus persistence/rewards, Pluto and Neptune each have focused mission files, and Uranus 1/2/3 are separate because Uranus 2 is independently large. Mission 6 and Titania 1/2 remain in their dedicated `Neptune3PressingForward.cs`, `Titania1Minesweeper.cs`, and `Titania2Beenoculars.cs` files.
- `LeveLTriggers.cs` is intentionally retained only as a compatibility stub at its legacy Unity asset path. Do not put campaign behavior back into it.
- The old `Neptune3PressingForward()`, `Titania1Minesweeper()`, `Titania2Beenoculars()`, `Titania1Ending()`, and `Titania2Ending()` implementations were obsolete duplicates that were not used by `CampaignMissionCatalog`; they were removed rather than migrated.

## Campaign test isolation

- `CampaignScenarioIsolation` is the process-wide guard for isolated `Space` scene tests and must be active before scene load so socket/audio bootstrap can suppress persistent/network side effects.
- `CampaignScenarioSceneHost` keeps real Stage/prefab/pool references but intentionally does not run full user-data/fleet bootstrap. Do not call full `CampaignMissionCatalog.Configure` from an isolated scene test until its `CurrentShips`/fleet dependencies are explicitly supplied or replaced.
- Full campaign-playthrough claims must share the real objective logic and real persisted-fleet semantics; source scans or test-only parallel mission graphs are not equivalent.
