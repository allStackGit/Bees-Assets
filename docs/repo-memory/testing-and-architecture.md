# Testing and architecture

## Unity/test boundary

- Production runtime scripts compile into Unity's predefined `Assembly-CSharp`. EditMode/PlayMode tests cannot reference that assembly directly, so existing tests intentionally use the `Tests/*/RuntimeAssembly.cs` reflection adapters.
- Prefer EditMode for deterministic state/lifecycle and real-prefab qualification that does not require rendered-frame progression. Use PlayMode for scene bootstrap, physics callbacks, destroyed-object semantics, async/frame progression, rendering/GPU behavior, and long-running soak work.
- Authoritative command-line validation is Unity Test Framework XML plus the exact executed-test count. Unity process exit/log noise alone is not sufficient evidence.
- The release gate is `Tools/Run-BeesReleaseGate.ps1`; slow qualification categories should stay opt-in so the normal correctness loop remains practical.
- Leave new Unity `.meta` generation to Unity during remote repository work.

## Source organization

- Large Unity components should be split along ownership boundaries before they become unsafe to edit through whole-file tooling. Partial classes are appropriate when the behavior still belongs to one Unity component and must share private state.
- Keep lifecycle/reset code close to the state it owns; move independent targeting, geometry, persistence, campaign, or diagnostics behavior into focused partials/helpers.
- Do not perform broad formatting/comment cleanup as a side effect of a bug fix. Large textual rewrites make regression review substantially harder even when executable behavior is unchanged.
- When sorting/shuffling data returned from runtime owners, verify whether the getter exposes the authoritative mutable collection. Make a snapshot (`ToList`) before reordering shared state.

## Campaign test isolation

- `CampaignScenarioIsolation` is the process-wide guard for isolated `Space` scene tests and must be active before scene load so socket/audio bootstrap can suppress persistent/network side effects.
- `CampaignScenarioSceneHost` keeps real Stage/prefab/pool references but intentionally does not run full user-data/fleet bootstrap. Do not call full `CampaignMissionCatalog.Configure` from an isolated scene test until its `CurrentShips`/fleet dependencies are explicitly supplied or replaced.
- Full campaign-playthrough claims must share the real objective logic and real persisted-fleet semantics; source scans or test-only parallel mission graphs are not equivalent.
