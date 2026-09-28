# Bees Engineering Invariants

These are cross-cutting rules future changes must preserve. Keep this file concise; detailed implementation knowledge belongs in `docs/DEVELOPMENT_MEMORY.md` and `docs/engineering/SYSTEM_MAP.md`.

## State and lifecycle

- A `Level` owns its runtime `GameState`; mutable battle state must not leak between levels, scenes, or restarted games.
- Pool reuse creates a new logical lifetime. `ClearData`/setup paths must reset all behaviorally relevant state, including timers, IDs, references, derived collections, async ownership, and flags.
- Kill/teardown/release paths must be idempotent where duplicate callbacks are possible. Deferred releases must drain exactly once.
- Static/global state used by tests or scenes must have an explicit ownership/reset strategy.
- Multiplayer ownership is Free Play-only. Campaign and Challenge must not inherit or create a `MatchSession`. Cross-scene multiplayer configuration uses the one-shot pending Free Play session handoff and must be consumed/cleared rather than retained as general global battle state.
- `MatchSession` peer/player/side/pre-spawn squad ownership is mutable only in the lobby phase.
- Lobby snapshots are perspective-neutral: `IsLocal` is never copied from another machine. A client must reconstruct exactly one local peer by matching its own canonical transport identity; duplicate peer IDs, duplicate transport identities, unknown player peers, invalid sides, or snapshots without a local player fail closed. The lobby also selects one `AuthorityPeerId`; battle start freezes it. Non-authoritative clients must not execute player commands locally, and only the authoritative peer may apply received command envelopes. Battle start freezes that configuration; disconnect/reconnect handling must preserve match player identity rather than silently rebuilding lobby ownership mid-battle.
- Lobby wire packets are size-bounded and exact-schema at both the top level and nested peer/player records; participant counts and transport-identity lengths are capped before a reconstructed session is accepted.
- Pre-battle Steam lobby synchronization uses a channel distinct from battle commands. Clients trust lobby snapshots only from the explicitly configured authority transport identity; hosts send only to peers already present in the configuring `MatchSession`. Lobby transport does not itself start a battle or write persistent profile data.
- Pre-spawn lobby squad ownership must use transient `SavedSquad.MatchOwnershipToken`, not persistent `SavedSquad.Id`; the token may survive in-memory cloning for scene handoff but must never be written into persistent squad JSON.
- Canonical multiplayer loadouts must never import another participant's persistent `SavedSquad.Id`, `FleetShip.Id`, or profile combat-history stats. Lobby synchronization rebuilds unsaved squads/ships with deterministic negative match-only IDs while preserving ship type, name, formation, squad settings, color, side, owner, and ownership token.
- `Squad.OwnerPlayerId` is match-local transient ownership, not persistent fleet identity or a network entity id, and must reset on every pooled squad lifetime.
- `Squad.MatchSquadId` is a match-scoped command/network identity: it resets on pooled object cleanup, but each new runtime squad lifetime in the same `MatchSession` receives a fresh monotonically allocated value. Player/network command routing must not treat pooled `Squad.ItemId` as a durable match identity.
- Hive Mind command scheduling must exclude every `Squad.IsPlayerControlled` squad, including remote or same-side co-op ownership; it must not infer human ownership only from `Configuration.UserSide`. Runtime `Ship.IsHiveMindControlled` must be derived from the owning squad before ship registration, not solely from the machine's primary `UserSide`.
- Squad selection and command dispatch are scoped by match player id. A non-primary player's selection must not enter the primary-local `SelectedSquads`/`IsSelected` UI state, and all player-scoped selections must be forgotten before pooled squad reuse.
- External/session-scoped player commands must fail closed for unknown players, dead/foreign squads, or squads owned by another player, and must enter gameplay through the ownership-validating `GameState.TryPlayer*` command gateway rather than directly invoking trusted `Squad.User*` methods. Received commands additionally bind the claimed `PlayerId` to the authenticated/source match peer before sequence validation, so one network peer cannot issue commands as another peer's player.
- Multiplayer command envelopes use positive, monotonically increasing per-player sequences. The authority accepts only the next contiguous sequence; gaps are rejected without advancing state, duplicates/older commands never reapply gameplay and are cumulatively re-acknowledged. Non-authoritative clients retain and retry commands until an acknowledgement from `AuthorityPeerId` removes them. Sequenced envelopes are invalid outside an active Free Play `MatchSession`.
- Multiplayer wire packets are versioned and bound to the current `MatchSession.MatchId` and positive `Level.MatchLevelId`; routing to the owning `GameState` must occur before command-sequence consumption. Oversized, malformed, schema-mismatched, or wrong-match packets must be rejected before they enter the received-command queue; the Bees P2P protocol is separate from the existing BeesServer request/response contract.
- Network/transport callbacks may enqueue copied command data only; they must not invoke Unity gameplay mutations directly. The per-Level received-command queue is bounded, cleared on Level reset/end, and drained with a per-frame budget on Unity's main thread.
- Non-authoritative outbound player commands are owned by one match-wide queue, not separate Level queues, because command sequence numbers are match/player scoped. Level reset removes only queued commands for that Level.

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
- Large fixed screen-relative `RectTransform`s must not leave controls attached to an obsolete reference rectangle. Convert viewport-like axes to stretch anchors while preserving intentional authored edge margins; do not stretch ordinary centered panels merely because they are large. Full-stretch anchors alone do not prove that a child is a viewport owner: deliberately inset panels must retain their authored offsets.
- Generic responsive repair owns viewport/screen-wrapper geometry only. It must not translate arbitrary UI islands or change meaningful sibling relationships. Semantic edge placement belongs to the subsystem that knows the control's intended role.
- Children driven by a Unity `LayoutGroup` remain under that layout group's geometry ownership. Generic responsive repair must not reanchor those children independently. A viewport-level layout owner must fill the actual canvas; when it contains one dominant fixed-height screen body plus fixed footer/tool rows, the dominant body must absorb taller-display surplus so the footer remains at the real bottom and the root backer cannot appear as a white strip.
- Responsive layout passes must be reversible across repeated display changes. Any repair that mutates `RectTransform` sizes or anchors must derive each new layout from immutable authored/reference geometry rather than treating the result of the previous repair as the next baseline; returning to a previous viewport must return to the same geometry instead of accumulating shrink or drift.
- Squad Maker placement has a separate interaction-space contract from its responsive presentation: the actual ship-placement workspace is always 600x340 logical units, centered in the responsive host and never stretched. An undersized host may uniformly scale that presentation down, but manual drag/drop, auto-drop, snapping, formation placement, hit validity, and persisted `SquadShip.Offset` remain in the same canonical world/logical coordinate system. Display resizing may reproject visuals only; it must not clear/rebuild squad membership or rewrite persistent offsets.
- A compatibility pass may move a whole direct root-canvas interactive island when its rendered bounds are outside the canvas. Explicit screen-edge navigation controls such as BACK/CONTINUE/SKIP may also receive a small rendering inset even when technically in bounds; the pass must not recursively translate arbitrary nested UI or invent a desktop safe-area inset.
- Gameplay HUD edge contracts are explicit and distinct from navigation-control margins: the scoreboard, ordinary Game Speed, selected-squad action box, and mini map sit flush with their intended canvas edges. Squad-number tabs start immediately to the right of the active scoreboard and otherwise at the top-left canvas edge; when a mission-objective panel is visible, the tabs must wrap to additional rows as needed rather than overlap it. A mission-objective panel with blank/whitespace text is not displayed. Do not add generic gameplay edge padding to solve clipping.
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
- Transient range/contact visibility state with multiple simultaneous observers must use ownership semantics; one observer exiting must not erase another observer's still-active contribution. Multiplayer player visibility is side-scoped; visibility learned by one physical side must not populate the other side's player-visible set.
- **Hive Mind learned visibility is deliberately different from transient visibility.** `GameState.VisionCache` and the side-wide Hive Mind environment caches are faction memory: once any Hive Mind observer sees a live enemy or environment object, the faction retains that knowledge for the remainder of that object's current `Level` lifecycle. An observer moving away, exiting range, or dying must not erase that learned sighting. The observed object's own removal/destruction or a `Level` reset/teardown may remove it.
- Physics- or frame-dependent behavior should be validated in PlayMode when EditMode cannot reproduce the Unity lifecycle contract.

## Testing and regressions

- Tests protect requirements, not implementation accidents.
- Every behavior change must classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**.
- A reproducible regression should gain a test that would have failed before the fix whenever practical.
- If automated coverage is impractical, the permanent regression record must explain why and state the strongest manual/system protection.
- Never treat a targeted green test as evidence that unrelated lifecycle, scene, persistence, network, or campaign contracts remain safe.
- New Unity engine APIs must be verified against the repository's documented Unity version before use. A code path that cannot compile in that version is a failed change even if its intended behavior and source-level tests are otherwise sound.

## Performance

- Performance improvements must preserve gameplay, cleanup, synchronization/ownership, save/network compatibility, and intended default quality.
- Prefer stable frame-time and bounded resource use over average-FPS-only wins.
- Do not introduce unbounded caches, retained pooled state, race conditions, or hidden quality reductions to improve a benchmark.

- SquadMaker lobby transport entry points are Free Play-only, must call the base `Scene.Update`, and must dispose their transport on scene teardown. Receiving a lobby session is not sufficient to start battle until canonical match/environment configuration is also staged.

- Online Free Play setup randomness is match-scoped and deterministic per `MatchLevelId`. Map/environment option resolution, random obstacle geometry, initial mining asteroid layout, and random squad composition/IDs must use the match setup stream; solo Free Play, Campaign, Challenge, RL, and ordinary combat randomness retain their legacy RNG paths.

- A configuring Free Play multiplayer lobby may span multiple Squad Maker scenes. Its one pending `MatchSession` may be peeked/resumed by pre-battle scenes but is consumed only by `Stage`; scene teardown disposes transport handles without discarding the configuring session.

- Canonical online Free Play level configuration is synchronized before battle and expressed in physical Bee/Human terms, not one machine's User/AI perspective. Stage remaps start positions per local perspective and forces `ChooseRandomLevel=false` so peers do not make a second independent level choice. Player, AI-initial, and AI-reinforcement squad roles are explicit; host-only persistent enemy squad IDs are resolved to transient compositions before wire serialization.
- Malformed canonical lobby level options fail at packet/session validation before Stage setup: map index, environment option ranges, generated squad count, text lengths, obstacle count, finite coordinates, and positive obstacle scales are bounded.

- `Ship.MatchShipId` is the match-scoped runtime identity for battle-state synchronization. It is independent of pooled `Ship.Id` and persistent/transient `FleetShip.Id`, is freshly allocated for each ship lifetime while a `MatchSession` exists, is indexed separately by `GameState`, and resets to zero on pooled cleanup.

- Authoritative battle-state packets use `MatchShipId` plus `MatchSquadId`, never pooled/runtime or persistent fleet identity. Packets are match- and level-bound, sequence-numbered, exact-schema, size/count bounded, reject duplicate ship IDs and non-finite motion values, and are created only by the local authority. Applying spawn/death side effects remains a separate contract and must not reuse normal persistence/stat-mutating kill paths implicitly.
- Non-authoritative live-ship correction is two-phase: validate the entire snapshot against the current `ShipsByMatchId` world before mutating any ship, then directly correct transform/velocity/cached rotation/health presentation without invoking damage, kill, persistence, score, or statistics paths. A ship-count/identity/type/side/squad/dead-state mismatch rejects the whole snapshot until explicit spawn/death replication exists.
- Steam battle-state replication uses channel 48, separate from pre-battle lobby channel 46 and player command/ACK channel 47. Only local authority sends state; a client accepts state only from its configured `AuthorityPeerId` and queues it for main-thread Level application.
- Replica despawn never enters normal `Ship.Kill`/`Squad.Kill`: it removes registry/visibility ownership, cancels timers, clears UI/selection, deactivates and queues pooled release without explosions, killer credit, score, persistent fleet/squad statistics, RL death diagnostics, or game-over evaluation. Authority snapshots may despawn local identities absent from the authoritative world before compatibility validation.
- `Squad.MatchSquadId` is indexed separately by `GameState.SquadsByMatchId` with duplicate rejection and reset/removal cleanup, so multiplayer lifecycle reconciliation never relies on pooled `ItemId` or persistent squad identity.
- Replica-created ships adopt authority-issued `MatchShipId` values before `GameState` registration and reserve allocator space to prevent future collisions. Replica setup runs the base ship setup path without virtual dispatch to derived gameplay setup, then cancels base/weapon timers, coroutines, Hive Mind/proximity behavior, and the Ship update loop while leaving presentation active for authority-state correction.
- Complete battle-state snapshots declare every live match squad before ships. Squad records carry match identity, physical side, owner, presentation/behavior flags and shooting strategy; ship records carry formation offset and minion/carrier role. Validation requires unique squad/ship IDs and every live ship to reference a declared same-side squad; dead ships are represented by absence, not as live-state records.
- Missing non-carrier replica squads/ships may be reconstructed from a validated complete authority snapshot using transient unsaved `SavedSquad`/`FleetShip` data and authority match IDs. Replica squad setup cancels autonomous chase/command activity; replica ship setup skips derived gameplay setup. Missing carrier squads/ships remain fail-closed until carrier-parent relationships are explicitly represented on the wire.
- Replica lifecycle reconciliation preflights the complete authority world before any spawn/despawn mutation. Unsupported carrier creation, owner/side mismatches, existing identity conflicts, or an authority ship tied to a locally disappearing squad reject the snapshot without mutation; only subsequent pool/resource failures may interrupt the mutation phase.
- Multiplayer protocol version 2 covers the complete battle-world snapshot shape. Carrier squads transmit their Drone/Striker type plus current parent Carrier `MatchShipId`; carrier children transmit the same parent identity. Validation binds ship types to physical sides, requires carrier children to match their carrier squad type/parent, and requires every nonzero parent to resolve to a same-side live Carrier.
