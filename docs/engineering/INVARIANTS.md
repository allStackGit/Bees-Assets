# Bees Engineering Invariants

These are cross-cutting rules future changes must preserve. Keep this file concise; detailed implementation knowledge belongs in `docs/DEVELOPMENT_MEMORY.md` and `docs/engineering/SYSTEM_MAP.md`.

## State and lifecycle

- A `Level` owns its runtime `GameState`; mutable battle state must not leak between levels, scenes, or restarted games.
- Pool reuse creates a new logical lifetime. `ClearData`/setup paths must reset all behaviorally relevant state, including timers, IDs, references, derived collections, async ownership, and flags.
- Kill/teardown/release paths must be idempotent where duplicate callbacks are possible. Deferred releases must drain exactly once.
- Static/global state used by tests or scenes must have an explicit ownership/reset strategy.

## Async and ordering

- Delayed/background work may mutate runtime state only after proving it still belongs to the current request and current pooled-object lifecycle.
- Older path requests must not overwrite newer destinations/results.
- Cancellation/teardown must not strand worker-slot ownership or let completed stale work publish later.
- Deterministic evidence must not rely on unordered collection iteration or cosmetic/global random-state side effects.

## Maps, prefabs, scenes, and assets

- Runtime lookup names are contracts. Map locations/configuration and map prefab names must remain deliberately aligned.
- `Resources` paths, serialized enum/name mappings, scene object references, prefab conversion dictionaries, and pool routing must be validated when renamed or reorganized.
- Mission-specific obstacle/map prefabs are gameplay, not decoration; pathing, clearance, hazards, visibility, and spawn geometry can alter mission behavior.
- Unity `.meta`/GUID relationships are asset identity. Remote edits must not casually regenerate or fabricate GUIDs for referenced assets.

## UI and display layout

- Screen-space UI must remain usable and correctly positioned across reasonable desktop resolutions and aspect ratios, including 16:9, 16:10, 3:2, 4:3 and ultrawide displays; the historic 1366x768 authoring size is a reference, not a required viewport.
- Root screen-space canvases must use resolution-independent scaling and live screen dimensions. Root canvases created after scene load require the same treatment as scene-authored canvases. Safe-area insets may be used only where the UI contract actually requires them; they must not push desktop edge HUD away from its intended screen edge.
- Large fixed screen-relative `RectTransform`s must not leave controls attached to an obsolete reference rectangle. Convert viewport-like axes to stretch anchors while preserving intentional authored edge margins; do not stretch ordinary centered panels merely because they are large.
- Generic responsive repair owns viewport/screen-wrapper geometry only. It must not translate arbitrary UI islands or change meaningful sibling relationships. Semantic edge placement belongs to the subsystem that knows the control's intended role.
- Children driven by a Unity `LayoutGroup` remain under that layout group's geometry ownership. Generic responsive repair must not reanchor those children independently. A viewport-level layout owner must fill the actual canvas; when it contains one dominant fixed-height screen body plus fixed footer/tool rows, the dominant body must absorb taller-display surplus so the footer remains at the real bottom and the root backer cannot appear as a white strip.
- A compatibility pass may move a whole direct root-canvas interactive island only when its rendered bounds are actually outside the root canvas; it must not recursively translate nested authored UI or add a desktop safe-area inset.
- Gameplay HUD edge contracts are explicit: visible edge controls need a small rendering inset rather than touching the exact canvas boundary; squad-number tabs start immediately to the right of the active scoreboard and otherwise use the top-left inset; the selected-squad action box remains fully visible at bottom-left; and the mini map remains fully visible at bottom-right.
- Pluto IV and Titania II share the Planetary Shield HUD and mission clock, so shield/clock/Game Speed layout changes must validate both missions. Pluto IV also displays the Evacuated counter in that cluster, while Titania II intentionally hides the counter.
- World-space UI is not part of the screen-layout normalization contract and must not be rewritten by screen-space compatibility code.

## Campaign and persistence

- Campaign mission identity cannot be inferred from a single source. Reconcile mission catalog/intro, current runtime data, trigger/objective code, exact authored assets, mechanics, dialogue/UI, and persistence effects.
- In-development missions must remain explicitly guarded until their real runtime/persistence dependencies are ready.
- Persistent fleet/squad/progress/stat data must remain attached to the correct user, mode, level, squad, and ship identity.
- A write failure or malformed input must not partially mutate a different persistence target.

## Networking

- Request hashes/deduplication are ownership mechanisms; cleanup must remove only hashes belonging to the owning lifecycle/level.
- Responses must be matched to the correct request type, request hash, level/game, and current pooled object identity before mutation.
- Reconnect/setup responses must update the level that owns the request, not an unrelated cached/current level.
- Unity/server protocol or version changes require checking the external BeesServer contract rather than assuming local compatibility.

## Combat/visibility/physics

- Repeated lethal/contact callbacks must not double-count statistics, damage outcomes, deaths, or pool releases.
- Range/visibility state with multiple observers must use ownership semantics; one observer exiting must not erase another observer's valid visibility/range contribution.
- Physics- or frame-dependent behavior should be validated in PlayMode when EditMode cannot reproduce the Unity lifecycle contract.

## Testing and regressions

- Tests protect requirements, not implementation accidents.
- Every behavior change must classify affected tests as still valid, update-required, obsolete-and-replaced, or missing.
- A reproducible regression should gain a test that would have failed before the fix whenever practical.
- If automated coverage is impractical, the permanent regression record must explain why and state the strongest manual/system protection.
- Never treat a targeted green test as evidence that unrelated lifecycle, scene, persistence, network, or campaign contracts remain safe.
- New Unity engine APIs must be verified against the repository's documented Unity version before use. A code path that cannot compile in that version is a failed change even if its intended behavior and source-level tests are otherwise sound.

## Performance

- Performance improvements must preserve gameplay, cleanup, synchronization/ownership, save/network compatibility, and intended default quality.
- Prefer stable frame-time and bounded resource use over average-FPS-only wins.
- Do not introduce unbounded caches, retained pooled state, race conditions, or hidden quality reductions to improve a benchmark.
