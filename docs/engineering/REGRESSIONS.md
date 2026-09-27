# Bees Permanent Regression Ledger

This file records fixed regressions that future work must not reintroduce. It is intentionally different from `BUG_LEDGER.md`:

- `BUG_LEDGER.md` contains current unresolved validated findings and should become empty as work is completed.
- This file contains durable root-cause/protection knowledge for regressions that have been fixed.

Do not use this as a chronological activity log. Add an entry when a real regression teaches a reusable lesson or needs permanent protection. Consolidate duplicate root causes.

## Required entry format

```markdown
### REG-001 — Short regression name
**Area:** subsystem/files/contracts involved  
**Symptom:** externally observable failure that revealed the regression  
**Root cause:** underlying technical reason, not merely the line that was wrong  
**Permanent protection:** focused automated test(s) that fail if the regression returns, or the strongest practical manual/system protection and why automation is impractical  
**Verification:** validation level that demonstrates the protection exercises the intended contract  
**Invariant/knowledge:** durable document or invariant updated because of the lesson, if applicable
```

Entries must not be added with an empty root cause, permanent protection, or verification field. If the cause is still unknown or the defect is still open, keep it in the active task/bug ledger instead of pretending it is permanently protected.

## Closure rule

A reproducible fixed regression is not fully closed until:

1. the underlying cause is understood;
2. a focused automated regression test exists whenever practical;
3. the test would have failed for the defective behavior rather than merely asserting the new implementation shape;
4. broader validation appropriate to the risk has been performed when execution is available;
5. any reusable ownership/lifecycle/architecture lesson is added to the appropriate invariant or durable-memory document.

Manual-only protection is acceptable only when the record explains why deterministic automation is not practical and names a concrete repeatable validation procedure.

## Entries

### REG-001 — Screen-space UI remained tied to the 1366x768 authoring rectangle
**Area:** `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, `Scripts/UI Components/RootCanvasCompatibilityGuard.cs`, root screen-space canvases, large legacy `RectTransform` wrappers, dynamically instantiated UI canvases  
**Symptom:** UI that was correct at the historic 1366x768/16:9 authoring size remained misplaced or failed to use the available screen correctly on Macs and other displays with different resolutions/aspect ratios.  
**Root cause:** the first responsive repair recognized only exact reference-sized containers with children. It therefore skipped leaf backers and large screen-relative frames such as 1366x668 layouts, and scene-load-only installation missed root canvases instantiated later. Protecting `LayoutGroup` children from independent reanchoring was necessary but not sufficient: on taller logical canvases, a full-screen vertical layout could still keep its original fixed child heights, leaving unused space that exposed the root backer below a fixed footer.  
**Permanent protection:** `Tests/EditMode/ResponsiveScreenLayoutGuardTests.cs` behaviorally exercises full-screen leaf panels, full-width/near-full-height frames with preserved margins, one-axis bars, and ordinary centered panels that must not be stretched. Late-created root canvases are discovered with the Unity-version-supported `Object.FindObjectsByType<Canvas>` API, and the shared discovery host installs both responsive-wrapper and final compatibility guards. `RootCanvasCompatibilityGuardTests` protects layout-owner stretching, layout-child non-interference, and the 718+50-style main-body/footer case where taller-display surplus must be assigned to the dominant body rather than left as an exposed strip.  
**Verification:** run the `BeesFoundation` EditMode category to execute the geometry regressions, then perform a representative rendered PlayMode/player check at 16:9, 16:10, 3:2, 4:3 and ultrawide sizes (including the affected Mac display). Runtime execution is still required after this remote change.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` defines resolution/aspect-ratio independence, late-created root-canvas coverage, the distinction between viewport-like wrappers and ordinary centered panels, and layout-owner versus layout-child geometry ownership.

### REG-002 — Responsive UI fix referenced an unavailable Canvas API
**Area:** `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, `Tests/EditMode/ResponsiveScreenLayoutGuardTests.cs`, Unity API compatibility  
**Symptom:** the project failed compilation with `CS0117: 'Canvas' does not contain a definition for 'allCanvases'`.  
**Root cause:** the late-created-canvas discovery path was written against an assumed `Canvas.allCanvases` API without verifying that API against the repository's current Unity version. The source-level regression test repeated the same invalid assumption, so the impact/test review did not catch the compile contract.  
**Permanent protection:** the discovery path now uses Unity 6's supported `Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None)`. The focused source regression explicitly requires that API and rejects `Canvas.allCanvases`; normal Unity compilation remains the authoritative protection against unavailable engine APIs.  
**Verification:** compile/import the project in the documented Unity version (`6000.5.4f1`) before running the `BeesFoundation` EditMode category; then perform the rendered cross-resolution UI checks required by REG-001. This remote change has not itself been compiled or executed.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` requires new Unity engine APIs to be verified against the repository's documented Unity version before use.

### REG-003 — Non-16:9 UI ownership and HUD collision handling remained incomplete
**Area:** `Scripts/UI Components/GameHudLayoutGuard.cs`, `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, `Scripts/UI Components/RootCanvasCompatibilityGuard.cs`, gameplay HUD, Main Menu, Squad Maker, Level Intro and other root-canvas screens  
**Symptom:** repeated display testing exposed several related regressions: gameplay edge controls were pulled inward, squad-number tabs could overlap the scoreboard, a later narrow-width layout let up to ten squad tabs collide with the mission-objective panel, a blank mission-objective panel could remain visible, and the authored inset Main Menu panel expanded toward its parent at runtime instead of keeping the spacing visible in the editor.  
**Root cause:** distinct UI ownership contracts were repeatedly conflated. Navigation controls benefit from a small rendering margin while gameplay HUD edges are intentionally flush. The live `Space` scene carries a stale `GameMenus.Scoreboard` reference to the inactive Summary panel, and scoreboard/mission-panel/tab geometry crosses sibling transforms. A one-row `HorizontalLayoutGroup` cannot adapt tab count to the live width between those HUD islands. Separately, `RootCanvasCompatibilityGuard` treated full-stretch anchors alone as proof that a `LayoutGroup` child was a viewport owner; that erased intentional offsets on inset panels such as the Main Menu even though the child did not actually represent the screen.  
**Permanent protection:** `GameHudLayoutGuard` uses zero gameplay edge margin; resolves the real live scoreboard; converts sibling scoreboard and mission-panel edges through world space; suppresses mission-objective panels whose text is blank/whitespace; disables the legacy one-row squad `HorizontalLayoutGroup`; and directly positions the at-most-ten tabs in as many rows as the live width allows, stopping before a visible mission-objective panel. `RootCanvasCompatibilityGuard` now distinguishes true viewport owners from merely full-stretch children: reference-screen-sized owners remain repairable, and a full-stretch child is treated as a viewport only when it essentially fills its parent, preserving deliberate inset offsets. `GameHudLayoutGuardTests` behaviorally protects scoreboard-relative start geometry, mission-panel right boundaries, wide single-row and narrow wrapped tab counts/positions, plus the empty-objective suppression source contract. `RootCanvasCompatibilityGuardTests` protects both true full-parent viewport recognition and rejection of an inset full-stretch MainPanel.  
**Verification:** compile/import in Unity `6000.5.4f1`, run `BeesFoundation`, then render Main Menu and representative gameplay at 16:9 and non-16:9 sizes including ultrawide. Main Menu must retain its authored spacing; gameplay edge controls must remain flush; blank mission-objective panels must be absent; and with 1–10 squad tabs, the tabs must remain one row when width permits and wrap to additional rows before overlapping a visible objective panel. Also repeat Level Intro/Squad Maker and the timed Pluto IV/Titania II HUD checks. Remote edits still require this Unity execution and rendered validation.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` and `docs/engineering/SYSTEM_MAP.md` distinguish true viewport ownership from full-stretch inset panels, flush gameplay edges from navigation margins, and record blank-objective suppression plus width-dependent squad-tab wrapping.

### REG-004 — Shared Pluto/Titania timed shield HUD drifted apart
**Area:** `Scripts/Levels/Level.Campaign.Pluto4.cs`, `Scripts/Levels/Level.Titania2Enhancements.cs`, `Scripts/UI Components/GameHudLayoutGuard.cs`, `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, Pluto IV and Titania II timed shield HUD  
**Symptom:** the green planetary-shield health fill could escape its rectangle, and later Pluto IV displayed Game Speed far to the left of the otherwise compact Planetary Shield/timer/Evacuated cluster. Titania II uses the same shield and timer controls, so a Pluto-only placement repair could regress Titania II.  
**Root cause:** generic responsive island translation originally moved pieces of the authored shield hierarchy independently. A follow-up repair then preserved Pluto IV with an absolute `-290` Game Speed x-position. That fixed one authored-size arrangement but detached Game Speed from the live shield geometry, so later responsive scaling made the fixed inset visibly wrong. The shared Pluto/Titania health-bar control also intentionally uses an established `0..150` scale convention and must not be reinterpreted as a Pluto-only `0..1` transform.  
**Permanent protection:** generic responsive repair does not translate ordinary HUD islands. Pluto IV clamps the health fraction before applying the shared `fraction * 150` convention. `GameHudLayoutGuard` now derives Pluto IV Game Speed placement from the live Planetary Shield and Evacuated-counter geometry: it right-aligns with the shield and top-aligns with the counter, falling back below the shield when no counter is visible. Titania II separately right-aligns Game Speed below the clock with its intended gap. The guard contains no Pluto-specific absolute x inset, and restores ordinary authored Game Speed placement whenever the mission clock is hidden. `GameHudLayoutGuardTests.TimedShieldAlignmentMathPreservesReferenceEdgesAndGap` behaviorally protects the geometry helpers, while the Pluto and Titania source-wiring regressions protect the two mission states and the shared shield-scale convention.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render both missions at representative 16:9 and non-16:9 sizes. For Pluto IV, check the timed state with Planetary Shield, timer, Evacuated counter and Game Speed together, including full/partial/depleted shield states and the transition out of the clock state. For Titania II, check Planetary Shield, timer and Game Speed with the counter hidden, again through changing shield health and mission completion.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` records that Pluto IV and Titania II share the Planetary Shield/timer cluster, while Pluto IV additionally owns the Evacuated counter; layout changes to this cluster must validate both missions.

### REG-005 — Legacy menu responsive ownership mishandled aspect-ratio surplus
**Area:** `Scripts/UI Components/LegacyScreenResponsiveLayoutGuard.cs`, `Scripts/UI Components/SquadMakerResponsiveLayoutGuard.cs`, Main Menu, Squad Maker, nested `HorizontalLayoutGroup`/`VerticalLayoutGroup` regions  
**Symptom:** Squad Maker exposed large blue horizontal or vertical gutters between its major regions at non-16:9 sizes. A subsequent Main Menu repair removed whole-menu letterboxing by stretching its direct interactive branch into all available canvas surplus, but on very wide or very tall displays that made the green authored menu panel grow to dominate the viewport instead of retaining its QHD presentation scale.  
**Root cause:** two different responsive ownership contracts were treated as the same problem. Squad Maker's screen-scale structural layout regions should absorb usable surplus. Main Menu's starfield/background owns extreme aspect-ratio surplus, while its centered interactive branch is authored as a bounded reference presentation and should not expand indefinitely merely because the root canvas is wider or taller.  
**Permanent protection:** `LegacyScreenResponsiveLayoutGuard` is restricted to Main Menu and preserves its bounded centered presentation. Squad Maker is owned separately by `SquadMakerResponsiveLayoutGuard`, which resolves the real serialized `ChosenSquadList` hierarchy, lets the native structural LayoutGroups own their children, preserves fixed side/footer dimensions, and assigns viewport surplus to the flexible body/center work region. `LegacyScreenResponsiveLayoutGuardTests` protect the Main Menu wide-screen maximum, tall/narrow uniform fit, restoration after aspect-ratio changes, and local-row exclusion; Squad Maker-specific layout tests protect native hierarchy ownership and repeated aspect changes.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render Main Menu and Squad Maker at QHD/reference 16:9 plus 16:10, 3:2, 4:3, portrait/tall and ultrawide sizes. Main Menu must retain the QHD-sized centered green presentation while extra extreme-aspect space remains starfield; smaller canvases may uniformly reduce the menu frame but must not stretch it. Squad Maker's major structural regions must continue using available screen space without blue/blank gutters, and local button rows must retain authored sizing. Remote edits still require this Unity execution and rendered validation.  
**Invariant/knowledge:** responsive ownership is hierarchical and role-specific: root backgrounds may fill the viewport, screen-scale structural work regions may absorb surplus, and authored centered presentation panels may remain bounded. Expanding a root viewport does not imply every nested branch should consume the same surplus.

### REG-006 — Squad Maker hover descriptions displaced campaign level details
**Area:** `Scripts/UI Components/SquadMakerResponsiveLayoutGuard.cs`, Squad Maker Chosen Squads column, START/TEST hover descriptions  
**Symptom:** at QHD the Squad Maker showed the Chosen Squads column and START/TEST controls but the campaign level title/details were missing from the right side.  
**Root cause:** the hover-description stabilization guard assumed START and TEST were mutually exclusive and kept the active button's invisible description GameObject participating in the right-column `LayoutGroup`. The Squad Maker can expose START and TEST simultaneously, so both invisible descriptions could remain active structural children and consume vertical layout space needed by the campaign level title/details.  
**Permanent protection:** `SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility` keeps hover descriptions active for stable pointer behavior but gives each a `LayoutElement` with `ignoreLayout = true`, making the descriptions visual overlays rather than structural rows. `Tests/EditMode/SquadMakerResponsiveLayoutGuardTests.cs` activates both descriptions simultaneously and verifies that both remain outside layout measurement while CanvasGroup hover visibility changes independently.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render the campaign Squad Maker at QHD and representative non-16:9 sizes. Confirm the level title/details are visible, START and TEST remain correctly positioned, and hovering either button shows its description without moving the column. This remote edit still requires Unity execution and rendered validation.  
**Invariant/knowledge:** START and TEST availability is not mutually exclusive. Their hover-only descriptions are visual overlays and must never reserve structural space in the Chosen Squads layout.

### REG-007 — Unity 6 WebGL WebSocket callbacks crashed on missing dynCall helpers
**Area:** `Plugins/WebSocket.jslib`, `Plugins/WebSocket.cs`, `Tests/EditMode/WebGlWebSocketBridgeTests.cs`, NativeWebSocket WebGL callback bridge  
**Symptom:** the WebGL player opened its browser WebSocket and then threw `TypeError: Module.dynCall_vi is not a function` from the `WebSocketConnect` `onopen` handler. The message, error, and close paths used the same legacy `dynCall_*` callback family and were vulnerable to the same runtime incompatibility.  
**Root cause:** the NativeWebSocket `.jslib` directly invoked managed function pointers through legacy `Module.dynCall_*` helpers. The first repair added a `.jspre` shim intended to recreate those helpers, but a rebuilt/deployed Unity 6 player still reached `Module.dynCall_vi` and failed, proving that shim was not a reliable contract for this toolchain. The durable interface is Emscripten's compile-time `makeDynCall` bridge with a signature matching each managed callback delegate.  
**Permanent protection:** `Plugins/WebSocket.jslib` invokes the open callback with `makeDynCall('vi', ...)`, the message callback with `makeDynCall('viii', ...)`, and both error/close callbacks with `makeDynCall('vii', ...)`; it contains no direct `Module.dynCall_*` calls. The ineffective `.jspre` shim was removed rather than retaining two callback mechanisms. `Tests/EditMode/WebGlWebSocketBridgeTests.cs` rejects legacy direct calls, requires the expected callback signatures/counts, and protects the `onMessage` slot spelling used by the bridge.  
**Verification:** run the `BeesFoundation` EditMode category for the source guard, then make a clean Development WebGL build with Unity `6000.5.4f1`, deploy all generated Build artifacts together, load it in a browser, connect to the normal Bees WebSocket endpoint, receive at least one server response, close/reconnect once, and confirm no `dynCall_*` exception occurs. The WebGL player build/browser execution remains required after this remote change.  
**Invariant/knowledge:** `docs/engineering/CONTEXT_INDEX.md` routes WebGL/browser WebSocket callback failures directly to the NativeWebSocket `.jslib` bridge and its managed callback signatures; Unity 6 callback interop must use the supported `makeDynCall` path rather than depend on legacy `Module.dynCall_*` exports.

### REG-008 — Unity 6 WebGL request tracking crashed in generic hash-table dispatch
**Area:** `Scripts/Server/StandingRequestSet.cs`, `Scripts/Server/Socket.cs`, `Scripts/ConfigData.cs`, `Scripts/Levels/Level.Reset.cs`, request resend/settings startup  
**Symptom:** the first WebGL failure on this path threw `MethodAccessException` from `IEqualityComparer<ServerRequest>.GetHashCode`. Replacing `HashSet<ServerRequest>` with a `Dictionary<long, ServerRequest>` removed that exact exception, but a later Development WebGL build still halted immediately after the user-ID log. Its symbolized stack identified `Dictionary_2_TryInsert -> Dictionary_2_Add -> Socket_LogRequest -> ServerSettings_Fetch -> Configuration..ctor`, proving request tracking itself was still the failing path.  
**Root cause:** request identity is the explicit `long ServerRequest.Hash`, but both attempted hash-table implementations routed lifecycle bookkeeping through generated generic comparer/interface machinery in Unity 6 WebGL IL2CPP. `HashSet<ServerRequest>` failed through `IEqualityComparer<ServerRequest>`; the replacement `Dictionary<long, ServerRequest>` then failed during `Dictionary.TryInsert` interface dispatch. The first repair therefore changed the generic key type without actually removing the unstable hash-table dispatch boundary.  
**Permanent protection:** `ServerRequestSet` now uses a small `List<ServerRequest>` and compares `request.Hash` directly for add/remove/contains/lookup/intersection semantics. `Socket` continues to use that abstraction for standing and waitable requests, `ConfigData.__PastServerRequests` continues to use it for request history, and reset pruning materializes a list rather than a hash set. `SocketResponseOwnershipTests` protects equivalent-hash removal and duplicate-hash ownership. `SocketPerformanceStructureTests.ServerRequestTrackingAvoidsIl2CppHashTableComparerDispatch` and `WebGlWebSocketBridgeTests.WebGlRequestTrackingAvoidsHashTableComparerDispatchOnIl2Cpp` reject both `HashSet<ServerRequest>` and `Dictionary` reintroduction on the request-set path while preserving explicit transport-hash identity.  
**Verification:** compile/import in Unity `6000.5.4f1` and run the `BeesFoundation` EditMode category. Then make a clean Development WebGL build, deploy all generated Build artifacts together, and verify `Configuration` construction proceeds through `Socket.LogRequest` without `Dictionary.TryInsert`, comparer `MethodAccessException`, or `RuntimeError: index out of bounds`; confirm settings responses complete and leave the player connected long enough to exercise timeout/resend bookkeeping. Runtime browser verification remains required after this remote change.  
**Invariant/knowledge:** request `Hash` is the transport identity. On this Unity 6 WebGL/IL2CPP path, changing hash-table key types is not sufficient protection; request lifecycle tracking must avoid the generic hash-table comparer/interface dispatch boundary itself. `docs/engineering/CONTEXT_INDEX.md` routes both comparer exceptions and symbolized `Dictionary.TryInsert -> Socket.LogRequest` failures here.

### REG-009 — WebGL startup retained unsupported runtime-bound dispatch
**Area:** `Scripts/Server/Socket.cs`, `Scripts/Server/ServerRequest.cs`, `Scripts/Settings/ShipStats.cs`, `Scripts/Settings/Configuration.cs`, `Scripts/Settings/StartingSettings.cs`, `Scripts/Data/UserData.cs`, `Scripts/Data/AotJson.cs`, profile data loaders, WebGL/IL2CPP startup  
**Symptom:** while investigating the opaque startup `RuntimeError: index out of bounds`, source review found C# `dynamic` on settings/profile parsing and outbound request serialization. Removing those paths did not eliminate the same browser crash; the later symbolized Development WebGL stack traced that crash to REG-008 request tracking instead.  
**Root cause:** independently of REG-008, the WebGL startup path contained C# `dynamic` in both directions. Settings/profile responses used runtime-bound Newtonsoft member access, and the shared outbound serialization boundary was `Socket.Send(dynamic content)`. IL2CPP is ahead-of-time and does not provide the JIT/runtime binder expected by C# `dynamic`, making these paths an unsupported latent WebGL compatibility boundary even though they were not the root cause of the subsequently symbolized `Socket.LogRequest` crash.  
**Permanent protection:** settings/profile bootstrap parses through explicit `JObject`, `JArray`, and `JToken` access; `AotJson` centralizes typed conversions; `UserData`, `DataFile`, and the base `ServerRequest.Request` placeholder use `object`; and `Socket.Send` accepts `object` while continuing to serialize the concrete runtime payload with `JsonConvert.SerializeObject`, preserving the wire shape. `WebGlWebSocketBridgeTests.StartupServerSettingsParsingAvoidsDynamicDispatchOnIl2Cpp`, `ProfileBootstrapParsingAvoidsDynamicDispatchOnIl2Cpp`, and `WebGlRequestSerializationAvoidsDynamicDispatchOnIl2Cpp` reject reintroduction of runtime-bound dispatch on these startup paths. Existing persistence/network tests remain valid because request types and serialized fields are unchanged.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then make a clean Development WebGL build and deploy all generated Build artifacts together. Confirm settings and profile bootstrap completes without runtime-binder/dynamic-dispatch failures. The Development trace from August 18, 2026 identified REG-008 as the then-current `index out of bounds` root cause, so REG-009 should not be used to diagnose that exact symbolized `Socket.LogRequest` stack.  
**Invariant/knowledge:** WebGL startup send and parse paths must be IL2CPP/AOT-safe. `docs/engineering/CONTEXT_INDEX.md` routes runtime-bound JSON/serialization concerns here, while symbolized request-tracking hash-table failures route to REG-008.

### REG-010 — Secure Socket factory skipped runtime field initialization
**Area:** `Scripts/Server/SecureSocketFactory.cs`, `Scripts/Server/Socket.cs`, WebGL WSS startup, production WSS startup  
**Symptom:** after REG-008 removed the crashing request hash table, the WebGL player advanced farther but then emitted repeating `ArgumentNullException: Value cannot be null. Parameter name: collection` and intermittent `sourceArray` failures during startup/update processing.  
**Root cause:** `SecureSocketFactory.CreateWebGl` and the production secure factory allocated `Socket` with `FormatterServices.GetUninitializedObject`. That deliberately bypasses the `Socket` constructor and therefore every instance field initializer. The factory manually restored several public fields, but private runtime-owned state such as `_waitableRequests` and `_waitableRequestSnapshot` was never initialized. Removing the earlier REG-008 crash exposed those null collections in the normal request/update path.  
**Permanent protection:** `Socket` now has an internal constructor that accepts the final WebSocket URL and secured flag while running normal field initialization before `MakeSocket()`. Both secure factory paths call that constructor directly; `SecureSocketFactory` contains no `FormatterServices`, reflection field patching, or `GetUninitializedObject`. `WebGlWebSocketBridgeTests.WebGlSocketFactoryRunsNormalSocketFieldInitialization` rejects constructor bypass and protects the required initialized request-tracking fields.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then make a clean Development WebGL build. Verify startup no longer repeats `ArgumentNullException` for `collection`/`sourceArray`, completes all settings/profile requests, and continues into the normal menu/game flow. Production WSS should also receive a representative connection smoke test before release because it previously used the same unsafe factory allocation path. Runtime verification remains required after this remote change.  
**Invariant/knowledge:** runtime owners that depend on field initializers must never be created with constructor-bypassing serialization APIs. Secure connection setup must supply the final WSS URL before construction rather than allocate an uninitialized `Socket` and patch fields afterward; `docs/engineering/CONTEXT_INDEX.md` routes these null-collection startup symptoms directly here.

### REG-011 — Squad Maker bootstrap recaptured responsive geometry as authored baseline
**Area:** `Scripts/UI Components/SquadMakerResponsiveLayoutGuard.cs`, `Tests/EditMode/SquadMakerResponsiveCanvasOwnershipTests.cs`, Squad Maker scene-load bootstrap and responsive reference ownership  
**Symptom:** the Squad Maker responsive owner could lose its immutable authored baseline during startup, making subsequent resolution/aspect changes restore from geometry that had already been rewritten by the responsive pass instead of from the serialized composition. This is a direct drift/shrink risk in the same failure family as the progressively shrinking/disappearing Squad Maker panel.  
**Root cause:** `HandleSceneLoaded` creates `SquadMakerResponsiveLayoutGuard` with `AddComponent`, which synchronously invokes `Awake`; `Awake` initializes the guard, captures authored geometry, and applies responsive geometry. Control then returns to `HandleSceneLoaded`, which explicitly calls `Initialize` again. The old initialization path cleared `_referenceGeometryCaptured` and captured a second baseline from the already-responsive RectTransforms, violating the immutable-reference invariant.  
**Permanent protection:** initialization is idempotent after the same `SquadMaker` and owned Canvas have successfully captured reference geometry; it can still retry when capture failed or when Canvas ownership genuinely changes. `SquadMakerResponsiveCanvasOwnershipTests.ReinitializingSameGuardDoesNotRecaptureResponsiveGeometryAsAuthoredBaseline` reproduces the `AddComponent -> Awake -> explicit Initialize` sequence and asserts that the original fixed `sizeDelta` remains the stored baseline after the second call. `SquadMakerSerializedLayoutContractTests` separately continues to protect repeated reference/wide/tall/revisit cycles through the real native LayoutGroup ownership contract.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render the regular Unity Squad Maker at reference/QHD plus 16:10, 3:2, 4:3, ultrawide and tall sizes. Repeatedly cycle between at least one wide, one tall and the original size and confirm the body, footer, three main columns and saved/chosen squad columns return to the same geometry without progressive shrink, disappearance or gutters. This remote edit still requires Unity execution and rendered validation.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` already requires responsive passes to derive from immutable authored/reference geometry. `docs/engineering/CONTEXT_INDEX.md` now also records the Squad Maker-specific bootstrap rule that the duplicate `Awake`/scene-loaded initialization for the same owner and Canvas must not recapture responsive output.

### REG-012 — Nested Squad Maker work regions allocated surplus without resizing their panels
**Area:** `Scripts/UI Components/SquadMakerResponsiveLayoutGuard.cs`, `Scripts/UI Components/SquadMakerLevelDetailsFitGuard.cs`, `Scripts/Scenes/SquadMaker.cs`, `Scenes/Squad Maker.unity`, `Tests/EditMode/SquadMakerSerializedLayoutContractTests.cs`, `Tests/EditMode/SquadMakerLevelDetailsFitGuardTests.cs`, center Squad Settings/Composition regions and Chosen Squads column  
**Symptom:** after the outer Squad Maker body and columns were made responsive, ultrawide windows still showed a large orange area to the right of the gray center work panels, while tall windows showed orange bands between and below the gray `Squad Settings`/`Squad Composition` panels. Later, the campaign level-details state showed another ownership error: the chosen-squads list consumed tall-screen surplus while the level report/supply-capacity area remained near its authored height, leaving text or the lower summary visibly cut off.  
**Root cause:** responsive ownership stopped one hierarchy level too early. The real `Squad Maker Column` is itself a `VerticalLayoutGroup` owner with two direct 620-wide children authored at 298 and 420 high. Its inherited configuration had `childControlWidth/Height = false` while force-expand remained enabled, so Unity distributed surplus in layout allocation but did not resize those child RectTransforms, exposing the orange parent in the allocated gaps. Separately, `SquadMaker.ToggleLevelOptions`/`ToggleLevelDetails` intentionally write semantic chosen-list heights of 663/415/278; replacing those values outright would fight the scene controller. The first responsive repair then applied all positive tall-screen surplus to every chosen-list state, including the 278-high level-details state, which starved the active level-details row instead of enlarging the report area.  
**Permanent protection:** `SquadMakerResponsiveLayoutGuard` includes `Squad Settings` and `Squad Composition` in the resolved serialized ownership contract. The center column's native vertical layout controls both child axes, keeps the settings region at its authored 298 height, and gives the composition region flexible height so it absorbs all remaining center-column space; both regions fill the live center width. The chosen-squads ScrollView always keeps the latest semantic base written by `SquadMaker`; normal/options states may layer positive height beyond the authored 718-high Chosen Squads column onto the list, but when `LevelDetailsContainer` is active the list remains at its semantic base so the lower report can own the surplus. `SquadMakerLevelDetailsFitGuard` then sizes the active details row to all remaining column height, shrinking only as needed to preserve the text's measured minimum on short viewports. `SquadMakerSerializedLayoutContractTests` protects both ordinary list-surplus behavior and the level-details exception, while `SquadMakerLevelDetailsFitGuardTests` protects deficit handling, tall-surplus expansion, and reversibility.  
**Verification:** compile/import in Unity `6000.5.4f1` and run the `BeesFoundation` EditMode category. Then render Squad Maker at QHD/reference, the reported ultrawide shape, the reported tall/portrait shape, 16:10, 3:2 and 4:3. The gray settings/composition surfaces must cover the full center work column with no orange bands; the settings region must remain bounded while composition uses extra height. In the campaign level-details state, the chosen-squad list must not grow at the expense of the report, all level text must remain visible, Supply Capacity must remain fully visible above START/TEST, and extra vertical space should appear in the details/report region. Normal/options chosen-list states must still use legitimate extra height. Repeated wide/tall/reference cycles must return to identical geometry. Runtime execution remains required after this remote change.  
**Invariant/knowledge:** responsive layout ownership is recursive and state-aware at structural boundaries. Semantic state writers retain ownership of their base sizes; responsive layout may layer viewport surplus only onto the semantic region that is intended to expand in the active state.

### REG-013 — Squad Maker header used the text field as the toolbar background
**Area:** `Scripts/UI Components/SquadMakerCompositionLayoutGuard.cs`, `Scripts/UI Components/SquadMakerHeaderBackdropGuard.cs`, `Tests/EditMode/SquadMakerSemanticResponsiveTests.cs`, `Tests/EditMode/SquadMakerHeaderBackdropGuardTests.cs`, Squad Maker composition header  
**Symptom:** assigning all ultrawide surplus to the squad-name input made the green editable field span almost the entire center workspace. After a separate full-width backdrop was added for the real direct-under-`Squad Composition` hierarchy, the compact Supply/name/COLOR/count group still remained pinned to the left edge of that much wider toolbar instead of being centered.  
**Root cause:** the visual contract of a full-width toolbar was conflated first with the sizing contract of one interactive child and then with the placement of the compact control group. The toolbar surface, control sizing, and group alignment are separate responsibilities. The real scene also has a direct hierarchy where the four controls meet only at `Squad Composition`, so a synthetic nested header-owner test did not cover the actual placement case.  
**Permanent protection:** `SquadMakerCompositionLayoutGuard` retains the existing bounded sizing/gap rules and does not turn the name input into the toolbar. Nested header owners can carry their own non-interactive backdrop. For the real direct hierarchy, `SquadMakerHeaderBackdropGuard` supplies the full-width theme-matched backdrop and translates the existing Supply/name/COLOR/count branches together so their rendered union is horizontally centered; it does not resize any of those controls. `SquadMakerSemanticResponsiveTests.HeaderUsesFullWidthBackdropWithBoundedCompactControls` continues to protect bounded name width, gaps, narrow-layout scaling and repeated width changes, while `SquadMakerHeaderBackdropGuardTests.DirectCompositionHeaderGetsFullWidthBackdropAndCentersControlsWithoutResizing` explicitly protects the direct hierarchy, centering across repeated widths, preserved gaps, and unchanged squad-name size.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render Squad Maker at reference/QHD and the reported ultrawide shape. The header background must span the available center width; Supply/name/COLOR/count must read as one compact group centered within that bar; the squad-name field must keep the current bounded text-entry sizing behavior; and resizing wide/narrow/reference must not create overlap, large holes, size drift, or cumulative positional drift. Runtime execution remains required after this remote change.  
**Invariant/knowledge:** structural surfaces own viewport surplus; ordinary interactive controls remain bounded by their semantic role. A full-width toolbar should use a full-width toolbar surface, and a compact related control group may be translated as a unit for alignment without resizing its members or manufacturing giant gaps between them.

### REG-014 — WAN actor crashed when its trajectory queue became empty
**Area:** `Training/bees_wan_actor_worker.py`, `Training/bees_wan_actor_training_tests.py`, elastic distributed rollout actor lifecycle  
**Symptom:** after draining the last completed trajectory, the actor could fail instead of treating an empty queue as the normal end of collection.  
**Root cause:** the `except` clause looked up `Empty` on the individual queue instance. Python exposes this exception as `queue.Empty`, so handling an empty `Queue` could itself raise `AttributeError`.  
**Permanent protection:** `WanOptionTests.test_actor_collects_trajectories_until_queue_empty` supplies one item and then exercises the empty read; it requires the actor to return the collected trajectory normally.  
**Verification:** the changed handler and focused regression were reviewed statically against the imported `queue` module and ML-Agents trajectory-queue usage. The test was not executed.  
**Invariant/knowledge:** emptying an ML-Agents trajectory queue is an ordinary actor-loop condition and must be handled with the module-level `queue.Empty` exception.


### REG-015 — Capped training-log uploads blocked final flush
**Area:** `Training/bees_training_worker_agent.py`, `Training/bees_training_control_tests.py`, training-control log ingestion  
**Symptom:** `flush_all` retried to its pass limit for a log larger than the per-file upload cap, even after the server had received the full allowed prefix.  
**Root cause:** the uploader stopped sending at `MAX_FILE_UPLOAD_BYTES`, while pending-byte detection compared the uploaded offset to the full local file length. The remaining tail was intentionally outside the upload contract but was reported as permanently pending.  
**Permanent protection:** existing `test_training_log_uploader_caps_each_uploaded_file` uses an eight-byte file and a four-byte cap, calls `flush_all` twice, and requires only the first four bytes to be uploaded. Pending detection now compares offsets against the per-file upload limit.  
**Verification:** source and the existing focused test were reviewed statically against the shared cap semantics. The test was not executed.  
**Invariant/knowledge:** local bytes beyond the uploader's explicit per-file cap are outside the upload contract and must not prevent final flushing from completing.


### REG-016 — Minimap clicks used screen coordinates as world coordinates
**Area:** `Scripts/Levels/LevelInputManager.cs`, minimap navigation  
**Symptom:** clicking or right-clicking the minimap could map the pointer to an incorrect viewport location because screen-space pointer coordinates were passed to a world-to-local transform.  
**Root cause:** `Transform.InverseTransformPoint` expects world-space coordinates, while the EventSystem result supplies a screen-space position.  
**Permanent protection:** minimap navigation now converts the pointer through `RectTransformUtility.ScreenPointToLocalPointInRectangle`, using the canvas camera when needed, before mapping the local point to viewport coordinates.  
**Verification:** the input path and `Prefabs/UI/Mini Map Canvas.prefab` were reviewed statically; the prefab uses a screen-space overlay canvas. No test or gameplay run was performed.  
**Invariant/knowledge:** screen pointer coordinates must be converted with the target RectTransform and its canvas camera before viewport mapping.


### REG-017 — Live RL inference fired weapons during the Healing action
**Area:** `Scripts/Scenes/RlOneVsOneAgent.cs`, `Scripts/Scenes/RlLivePolicyAgent.cs`, shared combat policy actions  
**Symptom:** a deployed policy could fire while selecting Healing even though the training adapter suppressed weapon fire for that same action combination.  
**Root cause:** live inference applied each weapon-fire branch without consulting the special-action compatibility rule used during training.  
**Permanent protection:** live inference now uses `SpecialActionAllowsWeaponFire` before applying each weapon-fire action, keeping deployment semantics aligned with the trained policy.  
**Verification:** both action dispatch paths were reviewed statically. No tests or gameplay were run.  
**Invariant/knowledge:** training, evaluation, and production inference must interpret each action branch combination identically.

### REG-018 — Discarded WAN batches remained acknowledged for retry
**Area:** `Training/bees_wan_actor_training.py`, `Training/bees_elastic_wan_training.py`, their focused tests, WAN actor learner queue and epoch transitions  
**Symptom:** an actor could believe a batch had been accepted after a policy/control change discarded that batch from the learner queue, silently losing completed on-policy experience when the initial HTTP response was lost and retried.  
**Root cause:** the broker records `batch_id` in its accepted-batch deduplication map when queueing the batch. Generation changes drained the trajectory queue and cohort state and cleared those acknowledgements, but the duplicate fast path checked the cache before checking the current generation. A retry overlapping invalidation could therefore reuse a cached acceptance result after the queued batch was discarded.  
**Permanent protection:** `BrokerInvariantTests.test_discarded_batch_retry_is_rejected_as_stale` accepts a batch, resets control state, and retries the same payload; `BrokerInvariantTests.test_stale_generation_takes_precedence_over_duplicate_ack`, `BrokerInvariantTests.test_stale_generation_takes_precedence_at_final_queue_admission`, and `ElasticBrokerTests.test_stale_generation_takes_precedence_over_duplicate_ack` protect both duplicate checks when invalidation overlaps admission. Both brokers now validate the current control and policy generation before returning a duplicate acknowledgement.  
**Verification:** implementation and regressions were reviewed statically against broker enqueue, generation invalidation, deduplication, and actor retry paths. None of these tests was executed; runtime validation remains pending.  
**Invariant/knowledge:** a batch acknowledgement is valid only while that generation's queued/on-policy experience remains eligible for learner consumption. When policy/control changes invalidate queued batches, their deduplication acknowledgements must be invalidated too.

### REG-019 — Pluto II tutorial wording bypassed the squad-number pointer
**Area:** \`Scripts/UI Components/CampaignFeedbackAdjustmentGuard.cs\`, \`Scripts/Levels/Level.Campaign.Pluto.cs\`, \`Tests/EditMode/InitialDesignPresentationRegressionTests.cs\`, Pluto II tutorial feedback  
**Symptom:** the squad-number tutorial page did not show the arrow pointing to the numbered squad controls.  
**Root cause:** the UI guard matched the obsolete phrase “Squads are assigned number hotkeys”, while the live mission sequence says “select squads with the number hotkeys on your keyboard”. The phrase test therefore never selected the arrow-presentation branch.  
**Permanent protection:** the matcher now keys on the stable “number hotkeys” phrase shared with the active tutorial copy. The presentation regression check ties that mission wording to the current guard predicate and arrow call. Its stale center-status assertion was also aligned to the current \`PlutoTwoTutorialPresentationGuard\` method signature.  
**Verification:** mission wording, guard predicate, and pointer-presentation call were reviewed statically. No test or gameplay run was performed; runtime display remains unverified.  
**Invariant/knowledge:** tutorial presentation predicates must match the actual authored page text; when mission copy changes, update any string-gated guard and its regression protection together.


### REG-020 — Turrets retained departed asteroid targets
**Area:** `Scripts/Entities/Ships/Ship.Movement.cs`, `Scripts/Entities/Ships/Weapons/Turret.Targeting.cs`, asteroid lifecycle  
**Symptom:** a turret could continue aiming and firing at an asteroid after the owning ship removed it from its nearby-asteroid set.  
**Root cause:** the ship removed an asteroid from `NearbyAsteroids` without clearing each turret's cached reference. Both `LeftNearbyAsteroid` and the periodic dead/null-entry pruning path could leave the cache intact; the firing guard checks object liveness/map entry but does not verify that the target remains nearby.  
**Permanent protection:** `MovementStaleStateTests.LeavingAsteroidClearsCachedTurretTarget` protects both detach and stale-entry pruning paths clearing the matching target and target flags.  
**Verification:** the removal, cache invalidation, and firing guard were reviewed statically. The regression test was added but not executed, per the static-only audit constraint.  
**Invariant/knowledge:** removing an asteroid from a ship's nearby set must also invalidate any turret target reference to that asteroid.


### REG-021 — Undefined numeric RL matchup modes were accepted
**Area:** `Scripts/Scenes/RlOneVsOneTrainingOptions.cs`, RL worker command-line configuration  
**Symptom:** numeric values such as `--rl-matchup-mode=999` could be accepted and flow into sampled-mode validation despite not naming a supported matchup mode.  
**Root cause:** `Enum.TryParse` accepts numeric representations, including undefined enum values, unless the parsed value is separately checked against the enum's defined members.  
**Permanent protection:** matchup-mode parsing now requires a defined `Fixed` or `Sampled` value. `RlOneVsOneTrainingOptionsTests.InvalidOrAmbiguousRlOptionsFailInsteadOfSilentlyUsingDefaults` covers both numeric zero and an out-of-range numeric value.  
**Verification:** the parser and focused regression protection were reviewed statically. Tests were not executed, per the static-only audit constraint.  
**Invariant/knowledge:** command-line enum options must reject undefined numeric enum values as well as unrecognized names.



### REG-022 — Initial episode-log tail scan skipped a complete record
**Area:** `Training/bees_training_worker_agent.py`, `EpisodeLogMetrics`  
**Symptom:** when a log exceeded the 4 MiB startup scan window and the selected offset landed exactly at a line boundary, the first complete episode record in the window was omitted from the reported metrics.  
**Root cause:** startup parsing always discarded the first decoded line whenever the scan began at a nonzero offset, assuming it was a partial record.  
**Permanent protection:** `Training/bees_training_worker_agent_tests.py` builds a log whose bounded scan begins exactly at a complete episode record and asserts that it is counted. The reader now checks the byte preceding the offset and discards text only when that offset is inside a record.  
**Verification:** focused regression coverage was added but not executed, per the static-only audit constraint.  
**Invariant/knowledge:** bounded log readers must distinguish an arbitrary interior offset from an exact record boundary before discarding input.

### REG-023 — Stale claim renewal overlapped a replacement actor session
**Area:** `Training/bees_elastic_wan_actor_session.py`, persistent actor-slot claim lifecycle  
**Symptom:** a replacement process on the same remote worker could have its initial slot claim rejected while the prior session's renewal request was still in flight after shutdown returned.  
**Root cause:** claim-keeper shutdown joined for two seconds even though broker requests can take up to the client's 30-second timeout; the old process could exit while its request was still in flight, allowing that delayed request to refresh the retired process identity.  
**Permanent protection:** `Training/bees_elastic_wan_training_tests.py` requires session shutdown to wait for the claim-keeper thread to finish before clearing its reference. `_stop_claim_keeper` now joins without a shorter timeout; the keeper's broker request has the configured finite timeout.  
**Verification:** focused regression coverage was added but not executed, per the static-only audit constraint.  
**Invariant/knowledge:** a session must not release actor-slot lifecycle ownership while an authenticated claim renewal is still in flight.

### REG-024 — Same-build rollout accepted a stale environment configuration
**Area:** `BeesServer~/trainingControl.js`, dedicated worker heartbeat identity  
**Symptom:** during a same-build environment-argument transition, a central learner could be marked rolled using its applied revision and build identity while still running the previous arguments. The remote actor could then roll to the new arguments and trigger promotion, leaving the learner and actor on different environment configurations.  
**Root cause:** rollout health checks did not bind the heartbeat to the environment arguments of the live managed process.  
**Permanent protection:** dedicated workers now report a SHA-256 identity for their active environment arguments. The server requires that identity to match the pending arguments before accepting a rollout acknowledgement. The focused server regression covers a stale central heartbeat followed by a correctly configured acknowledgement.  
**Verification:** regression coverage was added but not executed, per the static-only audit constraint.  
**Invariant/knowledge:** promote a same-build environment transition only after every required trainer proves its live process has the pending ordered argument list.


### REG-025 — Titania failure warning accumulated on each dialogue section
**Area:** `Scripts/CampaignDialogueOverrides.cs`, Titania Beenoculars dialogue  
**Symptom:** the loss-only evacuation warning could appear repeatedly in the same dialogue line after multiple dialogue sections.  
**Root cause:** `DialogueManager.StartDialogue` reapplies campaign overrides before every section, while the Titania I failure patch appended text to a shared `DialogueLine` rather than rebuilding its text.  
**Permanent protection:** the patch now constructs the full line from the route outcome and assigns it with `Set`, making repeated application idempotent. `CampaignDialogueDocumentSyncTests` now protects against mutation-by-append.  
**Verification:** regression coverage was added but not executed, per the static-only audit constraint.  
**Invariant/knowledge:** presentation-time overrides can run repeatedly; shared dialogue lines must be reset to deterministic content on each application.


### REG-026 — Campaign dialogue guard retained managers after scene unload
**Area:** `Scripts/CampaignDialogueOverrides.cs`, persistent `CampaignDialogueOverrideGuard`  \
**Symptom:** every campaign scene's `CutsceneManager` remained rooted by the guard's process-lifetime dictionary after its Unity scene unloaded, retaining managed wrappers and their referenced dialogue data across scene transitions.  \
**Root cause:** `_appliedMarkers` tracked managers to detect rebuilt dialogue lists, but never removed keys whose Unity objects had been destroyed.  \
**Permanent protection:** the guard now prunes destroyed managers every frame using Unity's destroyed-object null semantics before campaign-mode filtering, so cleanup also runs after leaving the campaign.  \
**Verification:** source-level lifecycle trace confirmed scene managers are added per scene and the override guard is installed with `DontDestroyOnLoad`; fix was inspected after commit. No tests or runtime checks were run, per the static-only audit constraint.  \
**Invariant/knowledge:** persistent Unity services that key collections by scene objects must prune destroyed wrappers even when the active mode changes.


### REG-027 — WAN actor reconnected before session threads had stopped
**Area:** `Training/bees_wan_actor_worker.py`, actor session shutdown  \
**Symptom:** an actor session could return from `close()` after a two-second join timeout while its uploader or state watcher remained inside a broker request, allowing the reconnect loop to start another session with the same actor identity and throughput output.  \
**Root cause:** broker calls use a 30-second socket timeout, but shutdown waited only two seconds for the background threads.  \
**Permanent protection:** `close()` now joins each started watcher/uploader thread before the session can be replaced and before final throughput metrics are written.  \
**Verification:** source inspection confirmed both threads use `BrokerClient`, whose `urlopen` call has a finite configured timeout, and the reconnect loop creates the next session only after `close()` returns. No tests or runtime checks were run, per the static-only audit constraint.  \
**Invariant/knowledge:** an actor must finish background ownership of broker requests and per-actor output before its process starts a replacement session.


### REG-028 — Stale supervisor health probe could kill a replacement server
**Area:** `BeesServer~/start-server.js`, managed supervisor health recovery  \
**Symptom:** if a server child exited while its asynchronous health probe was pending, the restart timer could install a replacement before the old probe returned; an old failure could then count against and terminate the replacement.  \
**Root cause:** the health callback applied its result to the mutable current `child` reference rather than to the child instance that the probe had checked.  \
**Permanent protection:** the callback captures the probed child and discards its result if the supervisor is stopping, the current child changed, or the probed child exited while awaiting the response.  \
**Verification:** source-level event ordering confirmed the restart can occur before the awaited health probe returns; the post-await identity guard prevents stale results from changing restart counters or signaling a replacement. No tests or runtime checks were run, per the static-only audit constraint.  \
**Invariant/knowledge:** asynchronous health results must be scoped to the process instance they observed before mutating supervisor state.


### REG-029 — Pluto II ships-range tooltip gained repeated trailing letters
**Area:** `Scripts/UI Components/CampaignFeedbackAdjustmentGuard.cs`, Pluto II campaign tutorial tooltip  \\
**Symptom:** the tooltip changed “ships’ range” to “ships’ ranges” on every frame, causing repeated trailing “s” characters (and the same issue for ASCII apostrophes).  \\
**Root cause:** the singular phrase remained a substring of the pluralized phrase, so the per-frame replacement was not idempotent.  \\
**Permanent protection:** the replacement now requires a word boundary after “range”; once the text is plural, the singular pattern no longer matches.  \\
**Verification:** the regex and its repeated per-frame application were reviewed statically for both apostrophe forms. No tests or runtime checks were run, per the static-only audit constraint.  \\
**Invariant/knowledge:** per-frame UI text corrections must not match their own output.


### REG-030 — Non-finite telemetry watcher interval stopped refreshes
**Area:** `Training/bees_continual_auto_train.py`, automatic public telemetry watcher configuration  \
**Symptom:** `NaN` or infinity could be accepted as the periodic watcher interval, causing its `Event.wait()` to fail or wait indefinitely while PPO training continued.  
**Root cause:** parsing validated only that the interval was greater than zero; comparisons with `NaN` do not reject it, and positive infinity also passes.  
**Permanent protection:** parsing now requires a finite positive interval. `AutomaticPublicTrainerOptionTests.test_watch_interval_must_be_finite` covers `nan`, `inf`, and `-inf`; the test was added but not run, per the static-only audit constraint.  
**Verification:** parser validation and the watcher’s `Event.wait(options.watch_seconds)` use were traced statically. No tests or runtime checks were run.  
**Invariant/knowledge:** every duration passed into a blocking wait must be finite and positive.  


### REG-031 — Continual service resumed a checkpoint under a changed contract
**Area:** `Training/bees_continual_service.py`, persistent optimizer/checkpoint lineage  \
**Symptom:** reusing a service `run_id` after changing its trainer config, continual compatibility config, training build, or environment arguments could resume the old ML-Agents checkpoint under the new training contract.  
**Root cause:** phase state and checkpoints were keyed by `run_id` alone; the service checked that a checkpoint existed but did not verify the configuration and environment that created it.  
**Permanent protection:** the service now persists a SHA-256 contract identity covering both configs, the training executable, game build label, generation size, environment count, and ordered environment arguments. Mismatches fail closed with guidance to use a new `--run-id`; legacy progressed state without an identity also fails closed. `ContinualServiceTests.test_changed_training_contract_cannot_resume_same_run_id` protects config drift. The test was added but not run, per the static-only audit constraint.  
**Verification:** state loading, checkpoint discovery, and the service's `--resume` decision were traced statically. No tests or runtime checks were run.  
**Invariant/knowledge:** an optimizer checkpoint may resume only when its persisted training contract still matches the active service configuration.  


### REG-032 — Socket request history grew until a level reset
**Area:** `Scripts/Server/Socket.cs`, `Scripts/Server/StandingRequestSet.cs`, stale squad-response lifecycle  \
**Symptom:** a long-lived online level accumulated completed server-request objects in process-wide `ConfigData.RequestHistory`; cleanup previously occurred only when resetting a level.  
**Root cause:** every request was appended to history, while the lifecycle guard only needs recent command/matchup requests to identify late responses for retired squad instances.  
**Permanent protection:** normal request logging now retains only command/matchup history and caps it at the same 4,096 entries kept during level reset. Explicit `WatchServerRequests` mode preserves the full diagnostic history. `SocketResponseLifecycleGuardTests.BoundedRequestHistoryRetainsTheNewestRequests` protects the bounded collection’s retention and duplicate behavior; the test was added but not run, per the static-only audit constraint.  
**Verification:** request creation, stale-squad lookup, and existing level-reset retention were traced statically. No tests or runtime checks were run.  
**Invariant/knowledge:** process-wide historical request tracking must stay bounded during normal play while retaining the newest entries needed for late-response ownership checks.  


### REG-033 — Distributed topology could allocate ports beyond TCP range
**Area:** `Training/bees_distributed_training.py`, ML-Agents worker port allocation  \
**Symptom:** a topology with a high `--base-port` and multiple environments could reach launch with one or more worker ports above 65535; local-only topologies bypassed the external-worker port validation entirely.  
**Root cause:** `training_topology` validated only that the base port was positive. The separate external-port check covered only selected remote workers, not the complete local and remote worker-ID span.  
**Permanent protection:** `training_topology` now rejects configurations when the final allocated worker port exceeds 65535. `bees_distributed_training_tests.py` covers local and mixed-topology overflow plus the highest valid two-worker boundary. Tests were added but not run, per the static-only audit constraint.  
**Verification:** ML-Agents assigns each worker a port offset from the base port; the topology's complete worker-ID range is contiguous from zero through `num_envs - 1`. Static review confirmed the upper bound is checked before factory/worker launch. No tests or runtime checks were run.  
**Invariant/knowledge:** validate the full span of environment ports for every distributed topology, including local-only configurations.


### REG-034 — Remote rollout worker could override its pinned environment args
**Area:** `Training/bees_remote_worker.py`, distributed worker session identity  \
**Symptom:** an inherited `BEES_TRAINING_ENV_ARGS_JSON` value could silently replace the Unity arguments from the hashed remote session spec, allowing remote environments to sample a different scenario/configuration from the central learner.  
**Root cause:** the helper treated the control environment variable as an unconditional override and never compared it with the identity-protected `unity_args` in the session spec.  
**Permanent protection:** remote workers now use the spec's pinned args and reject a nonempty control environment value when it differs. `bees_distributed_training_tests.py` covers matching and conflicting values. Tests were added but not run, per the static-only audit constraint.  
**Verification:** the remote command consumes `controlled_environment_args(spec["unity_args"])`; spec identity validation covers the pinned args, and static review confirmed mismatches fail before SSH or Unity children are launched. No tests or runtime checks were run.  
**Invariant/knowledge:** environment arguments that affect rollout distribution must remain bound to the remote session identity; inherited process state cannot override the hashed spec.


### REG-035 — Worker recovery discarded completed rollout steps
**Area:** `Training/bees_mlagents_learn.py`, batched ML-Agents environment stepping  \
**Symptom:** if an environment worker exited while ready responses were being drained, successful step responses already consumed from healthy workers were removed from the batch and never postprocessed.  
**Root cause:** the custom `SubprocessEnvManager._step` recovery branch cleared its accumulated step list and worker set on `ENV_EXITED`, even though those queue responses had already advanced healthy Unity environments.  
**Permanent protection:** failed-worker recovery now preserves completed healthy-worker responses and returns them for postprocessing before the next step cycle. `FastEnvManagerTests.test_worker_exit_does_not_discard_other_consumed_step_results` protects this ordering and queued restart response. The test was added but not run, per the static-only audit constraint.  
**Verification:** the queue-drain and recovery control flow were reviewed statically; successful responses remain in the returned batch, while restart handling schedules subsequent work. No tests or runtime checks were run.  
**Invariant/knowledge:** once a worker step response is consumed, preserve and postprocess its rollout result even if another worker fails during the same drain.


### REG-036 — Continual service resumed after trainer runtime code changed
**Area:** `Training/bees_continual_service.py`, Python trainer runtime and checkpoint lineage  \
**Symptom:** restarting a service with the same `run_id` after changing its trainer wrapper or supporting runtime module could resume optimizer state under different training behavior.  
**Root cause:** the persisted contract hashed configuration and the Unity environment binary, but omitted the Python code invoked to prepare telemetry, construct worker topology, and run ML-Agents.  
**Permanent protection:** the service contract now hashes the sorted production Python sources and dependency manifests under the configured runtime training root, and includes that root's resolved path plus the configured Python executable. Test modules are excluded. `ContinualServiceTests.test_changed_runtime_training_code_cannot_resume_same_run_id` changes the trainer wrapper between state save and load and requires resume to fail closed. The test was added but not run, per the static-only audit constraint.  
**Verification:** the source-file selection, deterministic relative-path/content hashing, persisted contract comparison, and regression case were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** resumable optimizer state must remain bound to the code and configuration that produce its rollouts and updates.  


### REG-037 — Worker restart backoff ignored changes to the desired launch
**Area:** `Training/bees_training_worker_agent.py`, managed child restart identity  \
**Symptom:** after repeated early exits, a corrected worker configuration that reused the same command could remain delayed by the previous launch's backoff.  
**Root cause:** backoff reset compared only the command tuple even though build identity, run identity, compatibility key, environment arguments, worker count, health/shutdown modes, and state-file path also define the launched process.  
**Permanent protection:** the worker clears backoff when any launch-defining value changes, then applies the delay only to a repeat of the same launch. `ManagedProcessRestartTests.test_changed_environment_args_bypass_same_command_backoff` exercises the unchanged-command case; the source-contract regression also protects build identity and state-file comparisons. Tests were added but not run, per the static-only audit constraint.  
**Verification:** desired launch values, stored child identity, restart delay, and process start ordering were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** restart backoff belongs to a complete launch identity, not merely the executable command line.  


### REG-038 — Unrelated control revisions bypassed managed-worker crash backoff
**Area:** `Training/bees_training_worker_agent.py`, managed child restart identity  
**Symptom:** a crash-looping worker could restart immediately when a shared training-control revision advanced, even though its launch command and all launch-defining settings were unchanged.  
**Root cause:** the restart-backoff identity treated the server's control revision as a launch change. Rollout phase transitions can advance that revision for workers that remain assigned to their existing launch.  
**Permanent protection:** restart backoff now resets only when the command, build/run/compatibility identity, environment arguments, state-file path, worker count, or child lifecycle modes change. `ManagedProcessRestartTests.test_control_revision_does_not_bypass_same_launch_backoff` protects the revision-only case. The test was added but not run, per the static-only audit constraint.  
**Verification:** shared server revision increments during rollout phases and the supervisor's restart identity were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** a control-plane revision alone is not evidence of a different launch and must not erase crash-loop backoff.

### REG-039 — Pluto II status centering followed the wrong campaign side
**Area:** `Scripts/UI Components/PlutoTwoTutorialPresentationGuard.cs`, `Tests/EditMode/InitialDesignPresentationRegressionTests.cs`, campaign UI  \
**Symptom:** the Pluto II status banner could be centered based on Human campaign progress even when the player's active campaign belonged to the Bee side, or fail to center when Bee-side progress was on Pluto II.  \
**Root cause:** the presentation guard checked `HumanSide`, while the neighboring campaign feedback guard and active campaign selection use `Configuration.UserSide`.  \
**Permanent protection:** the centering guard now checks the configured user side, and the source regression assertion requires `UserSide`.  \
**Verification:** compared both guards' mission-selection logic and inspected the updated source assertion statically. No tests or gameplay run were performed, per the static-only audit constraint.  \
**Invariant/knowledge:** campaign presentation tied to the player's current mission must use the configured user side, not a hard-coded side.
### REG-040 — Ship death left weapon targeting queues stale
**Area:** `Scripts/Entities/Ships/Ship.Combat.cs`, `Scripts/Entities/Ships/Weapon.cs`, weapon range and target-selection caches  
**Symptom:** after a target died or retreated, weapons could continue scanning a cached queue containing that removed ship and fail to select other ships still in range until a later range-enter/exit event invalidated the queue.  
**Root cause:** ship lifecycle cleanup either skipped incoming-range removal for retreating ships or removed a dead ship from each weapon's `ShipsWithinRange` dictionary without setting `HasCachedChanged`. `Weapon.MakeSortedTargetingList` reuses its cached queue while that flag is false; the queue can therefore retain a reference to the removed ship, and target validation rejects it without rebuilding the candidate list.  
**Permanent protection:** normal Ship and FireBarge cleanup now share one incoming-range cleanup method invoked for both death and retreat. It invalidates each weapon's targeting queue when it removes the ship. `CombatLifecycleIntegrationTests` covers both lethal damage and `EndKill()` return cleanup.  
**Verification:** the regression assertion was added and the death and retreat cleanup paths were reviewed statically. Tests and runtime validation were not run per the code-analysis-only instruction.  
**Invariant/knowledge:** any mutation of a weapon's range candidate set outside `RangeCollider` enter/exit callbacks must also invalidate `HasCachedChanged`.
### REG-041 — Deactivated weapon retained ship range contacts
**Area:** `Scripts/Entities/Ships/Weapons/RangeCollider.cs`, pooled weapon and ship range registries  
**Symptom:** a ship or weapon leaving active play could leave candidate entries in `Weapon.ShipsWithinRange` and reverse weapon references on candidate ships, retaining stale targeting state until physics exit callbacks happened.  
**Root cause:** `RangeCollider.Deactivate` cleared map-object visibility contacts but relied on trigger-exit callbacks to clean ship contacts while disabling its collider. The weapon dictionary and each ship's `WeaponsThatHaveUsWithinRange` are separately maintained registries, so disabling the collider alone did not guarantee both were cleared.  
**Permanent protection:** deactivation now removes the weapon from every candidate ship's reverse set, clears the candidate dictionary, and invalidates the targeting queue. `CombatLifecycleIntegrationTests.DeactivatingRangeColliderClearsBothShipRangeRegistries` covers both registry cleanup and cache invalidation.  
**Verification:** source and regression assertion reviewed statically; tests and runtime checks were not run under the code-analysis-only instruction.  
**Invariant/knowledge:** disabling a range collider must synchronously clear its ship contacts and reverse ownership rather than relying on later physics callbacks.
### REG-042 — Dying asteroids kept registering ship contacts
**Area:** `Scripts/Entities/CollisionAsteroid.Collisions.cs`, `Scripts/Entities/CollisionAsteroid.cs`, ship obstacle avoidance and turret asteroid targets  
**Symptom:** after asteroid health reached zero but before delayed removal, further trigger contacts could still register ships as nearby and ask them to avoid an already-doomed asteroid. Asteroid cleanup also notified nearby ships only when no destruction animation had been dropped, leaving stale proximity entries on other ships during collision-driven deaths.  
**Root cause:** collision handlers treated health-zero asteroids as eligible for the non-damage contact-registration branch. Separately, nearby-reference cleanup was incorrectly conditional on animation state and ignored `TouchingShips`, even though both sets feed each ship's `NearbyAsteroids` list.  
**Permanent protection:** ship and obstacle collision handlers now ignore dead or health-zero asteroids. Asteroid removal always tells both nearby and touching ships to clear the reference, then clears both contact sets. `MovementStaleStateTests.DeadAsteroidStopsRegisteringCollisionContactsAndCleansShipReferences` protects these source contracts.  
**Verification:** code paths and regression assertion reviewed statically. Tests and runtime checks were not run per the code-analysis-only instruction.  
**Invariant/knowledge:** health-zero asteroids stop accepting contacts immediately; removing an asteroid clears every ship proximity reference independent of presentation effects.
### REG-043 — Actor lease renewal allowed stale process takeover
**Area:** `Training/bees_elastic_wan_training.py`, dynamic WAN actor claims and registration ownership  
**Symptom:** a process using an old actor instance identity could reclaim a slot after a replacement process had registered. Manual actor IDs could also replace a still-active registration at the same slot. This could interrupt a healthy worker or create overlapping experience producers for one worker assignment.  
**Root cause:** automatic claim records were deleted as soon as registration succeeded, so the broker forgot which process instance owned the active lease. A later claim from any different instance could evict the registration. Manual registrations did not reject a different process identity when the slot was already occupied.  
**Permanent protection:** the broker keeps the current actor claim through registration and renews registration liveness with the claim lease. A different instance cannot take an active claimed slot; it may claim after the old lease expires. Manual slots also reject a different process identity while registered, and all registered actors must identify their process. Orderly shutdown now drains actor threads before releasing the exact process-owned registration and claim, so a healthy restart can reuse its slot immediately; crash recovery remains lease-expiry fenced. `ElasticBrokerTests` covers replacement after expiry, stale-process fencing, and immediate slot reuse after orderly release.  
**Verification:** control-flow and regression assertions reviewed statically. Tests and runtime checks were not run per the code-analysis-only instruction.  
**Invariant/knowledge:** one live process instance owns each actor slot for the duration of its lease; slot replacement must be fenced by lease expiry.

### REG-044 — Orderly actor shutdown dropped queued rollout batches
**Area:** `Training/bees_wan_actor_worker.py`, WAN actor upload queue and shutdown lifecycle  
**Symptom:** stopping or restarting an actor could discard a batch already placed in its bounded upload queue, or abort retries for an in-flight batch, before releasing the actor lease.  
**Root cause:** `ActorSession.close()` set the shared uploader stop flag, and `_upload_loop` used that flag as an unconditional exit condition instead of draining accepted in-memory work.  
**Permanent protection:** shutdown now gives the uploader a bounded drain window, retries transient backpressure/unavailability within that window, and joins it before orderly lease release. Session changes, stale ownership, permanent uploader errors, or the drain deadline still terminate the drain. `ElasticActorClaimShutdownTests.test_upload_loop_drains_queued_batch_after_shutdown_signal` protects the queued-batch case; `test_orderly_close_drains_session_then_stops_and_releases_lease` protects teardown ordering. Tests were added but not run, per the static-only audit constraint.  
**Verification:** uploader loop, stop signaling, bounded deadline, and actor lease release order were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** orderly actor shutdown drains pending rollout uploads for a bounded period and joins the uploader before releasing ownership; a crash or unavailable broker falls back to lease expiry.

### REG-045 — Same-launch exit could bypass managed restart backoff
**Area:** `Training/bees_training_worker_agent.py`, managed child restart reconciliation  
**Symptom:** if a child exited after the supervisor's last poll but before the next desired-state reconciliation, the next `ManagedProcess.start()` could clear the dead process reference and relaunch the same failing command without recording the exit.  
**Root cause:** `start()` treated an already-exited process as an ordinary stopped child; only the heartbeat polling loop called `record_exit()`.  
**Permanent protection:** a same-launch start now records a dead child's exit before evaluating its backoff. The replacement path also rechecks backoff if the child exits between the first check and `stop()`. `ManagedProcessRestartTests.test_same_launch_records_exit_observed_before_restart` protects the missed-poll case. The test was added but not run, per the static-only audit constraint.  
**Verification:** same-launch identity comparison, exit accounting, restart deadline, and the focused regression case were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** every unexpected child exit observed before a same-launch replacement contributes to that launch's backoff; unrelated control revisions do not clear it.

### REG-046 — Build preparation raced activation of the same artifact
**Area:** `Training/bees_training_worker_agent.py`, background build preparation and desired-build installation  \
**Symptom:** during a rolling release, the server can return the pending artifact as both `build` and `prepare_build`. The supervisor could then call `ManagedBuildStore.prepare()` on its background thread while `ensure()` installed the same artifact on the main thread, both replacing the same install directory.  \
**Root cause:** server state intentionally returns the pending build for preparation and, once that trainer becomes the rolling target, returns that same build as its active desired build. The worker store had no same-artifact serialization.  \
**Permanent protection:** when preparation and the active training target identify the same role/platform/build/hash, the supervisor waits for any in-flight matching preparation before activating the build. It still reports preparation readiness to the server. `BackgroundBuildPreparerTests.test_wait_for_build_joins_only_matching_preparation` protects the synchronization rule. The test was added but not run, per the static-only audit constraint.  \
**Verification:** current server `stateFor()` rollout phases, worker request/install ordering, and same-build join behavior were reviewed statically. No tests or runtime checks were run.  \
**Invariant/knowledge:** one shared build install identity must not be prepared and activated concurrently; matching background preparation must finish before activation.  \

### REG-047 — Non-finite continual checkpoint scan interval
**Area:** `Training/bees_continual_train.py`, candidate checkpoint monitor configuration  
**Symptom:** `--continual-scan-seconds=nan` or an infinite value passed the “positive number” validation. The candidate monitor then received an invalid/unbounded thread wait interval, which could stop periodic checkpoint discovery during training.  
**Root cause:** the parser rejected values `<= 0` but did not reject non-finite floating-point values.  
**Permanent protection:** scan intervals must now be finite and positive. `ContinualOptionTests.test_scan_interval_rejects_non_finite_values` covers NaN and both infinities. The test was added but not run, per the static-only audit constraint.  
**Verification:** the option parser’s float conversion, finite/positive guard, and its use as the monitor thread wait interval were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** any numeric duration passed to a blocking thread wait must reject NaN and infinities as well as zero and negative values.

### REG-048 — Compound ship names retained mid-phrase capitalization
**Area:** `Scripts/UI Components/DialogueManager.cs`, campaign dialogue text normalization  
**Symptom:** dialogue such as “The Fire Barge …” rendered the multiword ship type as “Fire barge” instead of lowercasing the complete ship type in ordinary prose.  
**Root cause:** the formatter replaced the shorter `Barge` name before `Fire Barge`, so the later compound-name replacement no longer matched.  
**Permanent protection:** compound ship names are normalized before their component names. `DialoguePresentationTimingTests.CompoundShipNamesAreNormalizedBeforeTheirComponents` verifies the rendered line. The test was added but not run, per the static-only audit constraint.  
**Verification:** traced the ordered replacements and statically reviewed the focused formatter regression assertion. No tests or runtime checks were run.  
**Invariant/knowledge:** when normalizing overlapping names by text replacement, process longer compound names before their components.  


### REG-049 — Wrong-type response could poison a live socket request hash
**Area:** `Scripts/Server/Socket.cs`, `Scripts/Server/SocketResponseLifecycleGuard.cs`, standing request ownership  \
**Symptom:** a response with a live request hash but a different request type could claim the hash as handled. The later correct response, which reuses that hash on retries, was then discarded as a duplicate while the standing request remained pending. Status handling could also retire or alter the mismatched request.  
**Root cause:** response deduplication and lifecycle decisions were keyed by hash before verifying that the standing request owned that hash for the response's request type.  
**Permanent protection:** response claiming now requires a matching standing request type, and the lifecycle guard suppresses mismatched types before any status-driven mutation. `SocketResponseOwnershipTests.WrongResponseTypeDoesNotClaimOrRetireTheStandingRequest` protects the live request, unclaimed hash, and subsequent valid claim. The test was added but not run, per the static-only audit constraint.  
**Verification:** traced response parsing, hash claiming, status handling, and request removal statically; the focused regression case was inspected but not executed.  
**Invariant/knowledge:** a response must match both the request hash and request type before it may claim deduplication state or change its owner's lifecycle.

### REG-050 — Elastic WAN ABI check could race local spec pinning
**Area:** `Training/bees_elastic_wan_slot_safety.py`, elastic WAN actor behavior-spec compatibility and broker registration  \
**Symptom:** a remote actor could register with behavior specifications incompatible with Exeter's local environment during startup, despite the intended early-registration compatibility check.  
**Root cause:** the local-spec compatibility scan released the broker condition before the parent setter pinned local signatures. A registration in that gap could become the first remote reference and then survive while the local signature was installed.  
**Permanent protection:** compatibility scanning and local signature pinning now run in one broker-condition critical section. `SlotSafetyTests.test_local_behavior_compatibility_check_and_pin_share_condition_lock` protects the atomicity boundary; the existing early-registration mismatch test protects rejection. The regression tests were added but not run, per the static-only audit constraint.  
**Verification:** the condition is created with Python's default reentrant lock, allowing the parent setter to re-enter it; both registration and reference pinning use that same condition. Source and regression assertion were reviewed statically. No tests or runtime checks were run.  
**Invariant/knowledge:** actor ABI compatibility must be checked and the local ABI pinned atomically against concurrent broker registrations.

### REG-051 — Weapon target ranking mixed map-local and world coordinates
**Area:** `Scripts/Entities/Ships/Weapons/Weapon.cs`, `Closest` and `Furthest` shooting strategies  \
**Symptom:** target ranking could compute incorrect distances when the level map transform was offset or otherwise transformed, changing which ship the weapon considered closest or furthest.  
**Root cause:** `Entity.GetPosition()` and weapon targeting positions are map-local, but `Collider2D.ClosestPoint` consumes and returns world-space coordinates; `Weapon.DistanceTo` passed and compared those values without converting coordinate spaces.  
**Permanent protection:** `Weapon.DistanceTo` now converts its map-local position through the level map transform, obtains the closest collider point in world space, and computes the distance in world space. `TargetingStrategyConsistencyTests.WeaponTargetDistanceUsesConsistentWorldSpaceCoordinates` protects this coordinate contract. The regression test was added but not run, per the static-only audit constraint.  
**Verification:** traced `Entity.GetPosition`, turret position conversion, Unity collider closest-point usage in line-of-fire checks, and the weapon sorting call path. Source and regression assertion reviewed statically; no tests or runtime checks were run.  
**Invariant/knowledge:** every input and output used with `Collider2D.ClosestPoint` must be world-space; convert map-local gameplay positions through the owning map transform first.

### REG-052 — Elastic WAN topology changes invalidated queued rollout lengths
**Area:** `Training/bees_elastic_wan_actor_session.py`, `Training/bees_elastic_wan_training.py`, `Training/bees_elastic_wan_zero_local.py`, elastic WAN actor/learner trajectory contract  
**Symptom:** when remote rollout capacity increased, the actor recalculated a smaller dynamic trajectory target while already-completed trajectories remained queued. The learner compared those batches against the smaller live target and aborted training, despite the batch still being within the configured ML-Agents `time_horizon` and generated under the same policy/control epoch.  
**Root cause:** the actor's dynamic segmentation target is allowed to change with total environment count, but a completed trajectory's size remains valid up to the fixed trainer `time_horizon`. Treating the learner's new dynamic target as a session-wide hard maximum incorrectly rejected in-flight batches from the prior topology.  
**Permanent protection:** both hybrid and zero-local elastic learners validate incoming trajectory lengths against the matching behavior's configured `time_horizon`, while preserving a strict upper bound and rejecting malformed/empty trajectories. `Training/bees_elastic_wan_training_tests.py` covers acceptance of an in-flight old-topology batch within that bound, rejection above it, and use of the guard by both learner paths.  
**Verification:** statically traced topology notifications, horizon recalculation, ML-Agents 1.1.0 `AgentProcessor` segmentation, actor upload queues, broker queueing, and learner injection. The regression tests were added and reviewed but not executed, per the static-only audit constraint.  
**Invariant/knowledge:** topology-dependent rollout targets are segmentation preferences; the configured `time_horizon` is the stable maximum for compatible queued experience across topology changes.


### REG-053 — Pathfinding setup rethrow obscured the original failure site
**Area:** `Scripts/Levels/Pathfinder.Search.cs`, path request setup diagnostics  
**Symptom:** exceptions thrown while selecting path worker grid nodes were rethrown with `throw e`, resetting the stack trace to the catch site and hiding the original failure location.  
**Root cause:** the catch block used the exception-variable rethrow form, which resets the recorded stack.  
**Permanent protection:** removed the redundant catch/rethrow wrapper so any setup exception propagates with its original stack.  
**Verification:** reviewed the current source and confirmed the catch/rethrow wrapper is gone. No tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** when an exception needs to propagate unchanged, use a bare rethrow or allow it to propagate; never use `throw exceptionVariable`.


### REG-054 — Guard command error logging reset the original exception stack
**Area:** `Scripts/Levels/Commands/Guard.cs`, guard movement diagnostics  
**Symptom:** a guard movement exception was rethrown with `throw e`, moving the reported failure site to the logging catch block and obscuring the original source line.  
**Root cause:** the exception-variable rethrow form resets the recorded stack trace.  
**Permanent protection:** retained the contextual error log and changed propagation to a bare rethrow, preserving the original stack.  
**Verification:** reviewed the current catch block and confirmed it logs context then uses `throw;`. No tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** diagnostic catch blocks should preserve the exception's original stack when rethrowing.


### REG-055 — Moving-asteroid overlap prevented clearance egress
**Area:** `Scripts/Levels/Pathfinder.Search.cs`, `Scripts/Entities/Ships/Ship.Movement.cs`, dynamic obstacle path recovery  
**Symptom:** after a ship overlapped a moving asteroid, its current grid cell could fail dynamic clearance while remaining safe in the static map. The path search requested egress, but the egress helper rejected every start that was not inside a static obstacle, so the ship could not plan out of the moving-asteroid clearance region.  
**Root cause:** egress eligibility was determined only from static signed clearance, even though the search enters egress whenever combined static/dynamic clearance at the start is insufficient.  
**Permanent protection:** egress now handles both static obstruction and dynamic-only clearance failure. Dynamic-only egress keeps every waypoint statically safe, never decreases dynamic clearance, and exits once the requested clearance is restored. `DynamicObstacleQualificationTests.ShipOverlappingMovingAsteroidCanFindClearanceEgressPath` covers a start cell blocked only by a moving asteroid. The test was added but not run, per the static-only audit scope.  
**Verification:** traced asteroid contact through `CollisionAsteroid.ShipCollision` and `Ship.FoundNearbyAsteroid` into path dispatch, then traced clearance-layer construction, worker search, and egress result assembly. Reviewed the updated algorithm and focused regression case statically. No tests or runtime checks were run.  
**Invariant/knowledge:** when a movement start fails combined clearance, recovery must distinguish static geometry from dynamic obstacles and preserve static-safe cells while escaping dynamic-only obstruction.


### REG-056 — RL movement heading was gated by a stale target

**Area:** `Scripts/Entities/Ships/Ship.Movement.cs`, `Scripts/Scenes/RlOneVsOneAgent.cs`, live and training policy movement

**Symptom:** a policy-selected heading could be ignored while a previously issued squad target remained within one ship-height, causing direct RL movement to continue in the old hull direction.

**Root cause:** `RlDirectionalMovement` consulted the legacy `HasTargetCoordinates`/distance state before applying the current `RlMovementDirection`. The policy controller updates the direction without owning or clearing that legacy target, so stale squad state could suppress a valid policy turn.

**Permanent protection:** `RlDirectionalMovement` now applies the policy heading on every movement update unless the policy explicitly selects the stop sentinel. `MovementStaleStateTests.PolicyHeadingIsNotGatedByAStaleMovementTarget` protects against reintroducing the stale-target gate.

**Verification:** traced policy action decoding through `ApplyMovementCommand` into `Ship.Move` and `RlDirectionalMovement`; confirmed the current heading is applied unconditionally and the focused source regression guard is present. The regression was not run, and no Unity or runtime behavior was checked, per the static-only audit scope.

**Invariant/knowledge:** while policy control owns movement, the current policy heading is authoritative; legacy squad target coordinates must not gate that heading.

### REG-057 — Telemetry expiry cleanup removed active upload files
**Area:** `BeesServer~/rlTelemetryUploads.js`, `RlTelemetryUploadManager.handle()` and `cleanupExpired()`  
**Symptom:** a slow chunk or completion operation could cross the idle timeout while another request ran cleanup, causing the session and partial file to be deleted before the active operation finished.  
**Root cause:** request handling awaited global expiry cleanup before reserving its authenticated session operation, while cleanup considered only the last completed activity timestamp and ignored queued/in-flight session work.  
**Permanent protection:** authenticated chunk/completion requests now reserve their session before the first asynchronous yield. Expiry cleanup skips sessions with active or queued operations; valid chunk activity continues to refresh the idle timestamp. `rlTelemetryUploads.module.test.js` holds a chunk operation open across the idle cutoff and checks that cleanup retains the session.  
**Verification:** statically traced request ownership, the per-session promise tail, cleanup ordering, and completion cleanup; reviewed the focused regression test. The test was added but not run, and no runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** idle expiry must not delete storage owned by an active asynchronous operation; reserve request ownership before yielding to cleanup.

### REG-058 — Expired telemetry quota records accumulated in memory
**Area:** `BeesServer~/rlTelemetryUploads.js`, telemetry upload rate limiting  
**Symptom:** every authenticated uploader left a quota entry in the long-lived server map after the user's rate window expired, so historical identities accumulated for the server lifetime.  
**Root cause:** quota windows were reset lazily only when the same user uploaded again; session expiry cleanup never removed inactive quota records.  
**Permanent protection:** `cleanupExpired()` now periodically removes quota entries whose per-user rate window has elapsed, while leaving fresh quota windows and active upload sessions untouched. `rlTelemetryUploads.module.test.js` creates quota records for two users, advances beyond their rate window but not the upload idle timeout, and checks quota reclamation without session expiry.  
**Verification:** statically reviewed the quota window reset and cleanup interval, and the focused regression test. The test was added but not run; no runtime validation was performed, per the static-only audit scope.  
**Invariant/knowledge:** per-user rate-limit bookkeeping in a long-lived process must be reclaimed after its enforcement window expires.

### REG-059 — Expiry cleanup removed active demonstration uploads
**Area:** `BeesServer~/rlDemonstrationUploads.js`, demonstration upload session ownership  
**Symptom:** a slow chunk or completion operation could cross the idle timeout while another request ran global cleanup, causing the session and partial archive to be deleted before the operation finished.  
**Root cause:** request handling awaited expiry cleanup before reserving the authenticated session operation, and cleanup used only `lastActivityAt` without considering queued or in-flight work.  
**Permanent protection:** authenticated chunk/completion requests now reserve their session before the first asynchronous yield, and expiry cleanup skips sessions with active or queued operations. `rlDemonstrationUploads.module.test.js` holds a chunk operation open across the idle cutoff and asserts that cleanup retains the session and permits the chunk to finish.  
**Verification:** statically traced request ownership, the per-session operation tail, completion-result cleanup, and the new focused regression. The test was added but not run; no runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** idle expiry must not delete files owned by an active asynchronous operation; reserve session ownership before yielding to cleanup.
  

### REG-060 — Failed model reads consumed download quota
**Area:** `BeesServer~/rlModelDistribution.js`, per-user model download quota  
**Symptom:** a transient file-open or read failure could consume the user's byte quota even though the server returned no model chunk.  
**Root cause:** `chunk()` reserved quota before opening and reading the bundle, but did not release that reservation when file I/O failed or returned fewer bytes than requested.  
**Permanent protection:** chunk reads retain the exact quota-window record they reserved against and refund the reserved byte count on failure; the refund is ignored if cleanup or a later request has replaced that user's window record. `rlModelDistribution.module.test.js` removes the bundle after metadata validation and verifies an open failure does not leave quota charged.  
**Verification:** statically traced successful and failed read paths, including concurrent reservations and rate-window replacement. The focused regression was added but not run; no runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** download byte quota measures bytes successfully returned as chunks; failed reads must release only their own reservation.

### REG-061 — Model cache missed same-size file replacement
**Area:** `BeesServer~/rlModelDistribution.js`, published pointer and bundle cache identity  
**Symptom:** replacing a current-deployment pointer with same-size content while preserving its modification time could leave the in-memory cache serving the previous deployment identity. The same cache check could miss a same-size bundle replacement with preserved modification time.  
**Root cause:** cached pointer and bundle identity was validated using only file size and modification time, which can be preserved across replacements.  
**Permanent protection:** cache records now include change time and inode for both pointer and bundle files; reuse requires all recorded identity metadata to match. `rlModelDistribution.module.test.js` caches a pointer, replaces its same-size contents, preserves modification time, and verifies that the new deployment is observed.  
**Verification:** statically traced cache reuse and invalidation for pointer and bundle records. The focused regression was added but not run; no runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** file caches that protect deployment identity must detect replacement even when size and modification time are unchanged.

### REG-062 — Pluto II tutorial controlled the hard-coded Human squads
**Area:** `Scripts/Levels/Level.Campaign.Pluto.cs`, Pluto II tutorial squad ownership  
**Symptom:** when the configured campaign user played as the Bee side, Pluto II's tutorial disabled and queried Human-side squads even though the tutorial squads were spawned for `UserSide`. The actual user squads could remain uncontrollable and tutorial triggers could wait on the wrong squads.  
**Root cause:** `Pluto2Reinforcements()` used `HumanSide` for its four tutorial squad lookups, while spawn placement and campaign routing use `UserSide`.  
**Permanent protection:** all four lookups now use `UserSide`. `InitialDesignPresentationRegressionTests.PlutoTwoTutorialOwnsSizingAndDialogueOrderBeforeRendering` isolates the Pluto II mission block and requires user-side lookups while rejecting hard-coded Human-side lookups.  
**Verification:** statically compared the mission's spawn side, four squad lookups, and existing campaign-side selection logic. The regression test was updated but not run; no Unity or gameplay checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** campaign tutorials must bind to the configured user side, which may be Bee or Human; hard-coded Human-side squad ownership is invalid.

### REG-063 — Cached distance targeting priorities became stale
**Area:** `Scripts/Entities/Ships/Weapons/Weapon.cs`, Closest/Furthest shooting strategies  
**Symptom:** weapons using Closest or Furthest could keep ranking targets by their old positions after ships moved, while all candidates remained in range.  
**Root cause:** cached targeting order was dynamically refreshed for changing damage/health priorities, but not for Closest/Furthest, even though target distance changes continuously.  
**Permanent protection:** cached queues now recalculate distance keys and resort for both Closest and Furthest. `TargetingStrategyConsistencyTests.ClosestAndFurthestTargetPriorityRefreshesForCachedQueues` protects the dynamic-priority contract.  
**Verification:** statically traced cached-queue reuse, priority refresh, distance-key recomputation, and target selection. The regression test was added but not run; no combat or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** target ranking strategies whose inputs change during movement must refresh when reusing a cached candidate set.

### REG-064 — Destination deduplication ignored stopped ships and path endpoints
**Area:** `Scripts/Entities/Ships/Ship.Movement.cs`, obstacle-aware movement order deduplication  
**Symptom:** with obstacle pathfinding enabled, a new destination close to the origin could be discarded after a ship stopped because the shortcut compared the requested destination to default/reset `TargetCoordinates` even though the ship had no active coordinate target. During path following, the same shortcut also compared against the current waypoint instead of the route's final destination.  
**Root cause:** `MoveToPoint` treated `TargetCoordinates` as a valid deduplication key without checking `HasTargetCoordinates`, and did not use `FinalDestination` when following a path.  
**Permanent protection:** the shortcut now runs only when a coordinate target is active and compares against `FinalDestination` while following a path, otherwise against `TargetCoordinates`. `MovementStaleStateTests.DestinationDeduplicationRequiresAnActiveMovementTarget` guards the source contract. The regression guard was added but not run, per the static-only audit scope.  
**Verification:** traced `StopMoving` resetting `TargetCoordinates` to zero and clearing `HasTargetCoordinates`, and traced path assembly setting `FinalDestination` while `IsFollowingPath` is true. The new guard and focused regression source were reread after the edits. No tests, builds, Unity, simulations, or runtime checks were run.  
**Invariant/knowledge:** movement deduplication must use an active command's destination, not default/stale target coordinates or an intermediate path waypoint.

### REG-065 — Pluto IV fleet tutorials described Human-side ship types
**Area:** `Scripts/Levels/Level.Campaign.Pluto4.cs`, `Scripts/Levels/GameState.Queries.cs`, Bluer Pastures tutorial dialogue selection  
**Symptom:** Pluto IV conditionally included dialogue about Dreadnoughts, Gunships, Frigates, and Scouts by inspecting the Human side. When the configured user plays as Bee, those messages described the opposing fleet rather than the user's fleet. The campaign catalog maps mission 3 to `Pluto4BluerPasturesCampaign`, so this affects the active mission implementation.  
**Root cause:** the mission called `GetHumanShipTypes()` for user-facing fleet dialogue even though campaign ownership is controlled by `Configuration.UserSide`.  
**Permanent protection:** added `GameState.GetUserShipTypes()` and changed both the active catalog mission and the retained public legacy Pluto IV implementation to use it. `CampaignTriggerStructureTests.PlutoFourFleetTutorialUsesConfiguredUserSide` guards both callers and the helper's side source. The regression guard was added but not run, per the static-only audit scope.  
**Verification:** traced campaign mission ID 3 to `Pluto4BluerPasturesCampaign`, confirmed `GetHumanShipTypes()` resolves to `HumanSide`, and reviewed the selected dialogue lines about the commander’s fleet. The changed source and regression guard were reread after editing. No tests, builds, Unity, simulations, or runtime checks were run.  
**Invariant/knowledge:** player-facing campaign tutorial content about the user's fleet must derive from `Configuration.UserSide`, which may differ from `HumanSide`.

### REG-066 — Uranus I carrier tutorial gate checked the wrong side
**Area:** `Scripts/Levels/Level.Campaign.Uranus1.cs`, `Scripts/Levels/GameState.Queries.cs`, carrier tutorial ownership  
**Symptom:** the carrier introduction was gated on whether the Human side had a Carrier, then selected carrier squads from `Configuration.UserSide`. If those sides differ, the tutorial could run with no user carrier to select or skip an available user carrier's introduction.  
**Root cause:** the trigger mixed a Human-side ship-type query with user-side squad selection.  
**Permanent protection:** the gate now uses `GetUserShipTypes()`, matching the side whose carrier squads the trigger selects. `CampaignTriggerStructureTests.UranusCarrierTutorialGateUsesTheConfiguredUserFleet` protects the gate and selection sides. The regression guard was added but not run, per the static-only audit scope.  
**Verification:** traced the trigger call in Uranus I, the carrier gate, user-side squad selection, and `GetUserShipTypes()` resolving to `Configuration.UserSide`. The changed source and regression guard were reread after editing. No tests, builds, Unity, simulations, or runtime checks were run.  
**Invariant/knowledge:** a tutorial's eligibility check and the entities it selects must refer to the same configured player side.

### REG-067 — Pluto I fetched spawned ships through fixed-faction lists
**Area:** `Scripts/Levels/Level.Campaign.Pluto.cs`, Pluto I `Pluto1Anomaly` ship ownership  
**Symptom:** the mission spawned its Scout on `UserSide` and Honeybee on `AISide`, then retrieved the first Human and Bee ships respectively and cast them to those mission ship types. With sides reversed, the Human lookup could be empty or the Bee lookup could return the player's Scout, causing the mission setup to fail or bind the wrong ship. The later Gunship reinforcement used the same fixed-Human lookup.  
**Root cause:** lookup ownership followed faction identity instead of the configured side used at spawn time, and first-ship retrieval did not filter by the requested type.  
**Permanent protection:** Pluto I now queries the configured user or AI side and filters by Scout, Honeybee, or Gunship as appropriate. `CampaignTriggerStructureTests.PlutoOneResolvesMissionShipsByConfiguredSideAndType` guards all three lookups. The regression guard was added but not run, per the static-only audit scope.  
**Verification:** traced each spawn side and ship type against its subsequent lookup, and confirmed `State.GetShips(int side)` returns the requested side's ships. The edited mission and regression guard were reread after editing. No tests, builds, Unity, simulations, or runtime checks were run.  
**Invariant/knowledge:** mission entities must be retrieved by the same configured side and ship type used to spawn them; faction-named convenience lists are unsafe when user and AI sides can be reversed.

### REG-068 — Uranus I proximity sensing was attached to Human-side Scouts
**Area:** `Scripts/Levels/Level.Campaign.Uranus1.cs`, player Scout proximity sensing  
**Symptom:** Uranus I attached proximity colliders to Human-side Scouts, but the mission's Bumblebee discovery trigger checks `UserSide` ships for those nearby-enemy records. With the user side set to Bee, user Scouts received no proximity collider while Human-side AI Scouts did, so proximity-based discovery tracked the wrong fleet.  
**Root cause:** the sensor setup loop used `GetHumanShips()` even though the consuming discovery condition is keyed to `Configuration.UserSide`.  
**Permanent protection:** the loop now attaches the generic side-agnostic proximity collider to Scouts from `UserSide`. `CampaignTriggerStructureTests.UranusOneProximitySensingIsAttachedToUserScouts` guards the configured-side source. The regression guard was added but not run, per the static-only audit scope.  
**Verification:** traced `ProximityCollider.IsEnemyShip` to confirm it compares actual ship sides, and traced the mission discovery trigger to confirm it reads only `UserSide` ships. The edited mission and regression guard were reread after editing. No tests, builds, Unity, simulations, or runtime checks were run.  
**Invariant/knowledge:** proximity state consumed as player perception must be attached to the configured user's ships, not a faction-named collection.


### REG-069 — Uranus III carrier tutorial gate checked the Human fleet
**Area:** `Scripts/Levels/Level.Campaign.Uranus3.cs`, optional carrier tutorial gate  
**Symptom:** Uranus III decided whether to show the carrier introduction by checking for a Human-side Carrier. The selected-carrier flow then checks/selects the configured user fleet. When the user side is Bee, a user-side Carrier could be present while the Human-side check skipped the tutorial, or a Human Carrier could trigger a tutorial with no user Carrier to select.  
**Root cause:** the mission gate used `GetHumanShipTypes()` while the tutorial selection is owned by `Configuration.UserSide`.  
**Permanent protection:** the gate now uses `GetUserShipTypes()`. `CampaignTriggerStructureTests.Uranus3HiveMindStartupDoesNotRequireCarrierTutorial` guards the user-side query and the no-Carrier HiveMind startup path. The guard was updated but not run, per the static-only audit scope.  
**Verification:** traced the Uranus III gate to `SelectedCarrierTrigger()`, which checks and selects carriers from the configured user fleet; confirmed the same gate also has a direct HiveMind startup path when no user-side Carrier is present. Source and guard were reread after the edits. No tests, builds, Unity, simulations, or runtime checks were run.  
**Invariant/knowledge:** eligibility checks for optional player tutorials must inspect the same configured side the tutorial controls.


### REG-070 — failed telemetry upload allocation consumed user quota
**Area:** `BeesServer~/rlTelemetryUploads.js`, authenticated telemetry begin and partial-file allocation  
**Symptom:** the server reserved the full declared upload size against the authenticated user before creating the partial file. If file creation failed, the request left the reservation charged for the rest of the rate window, preventing a retry despite there being no session or uploaded data.  
**Root cause:** the allocation failure path did not roll back its quota reservation.  
**Permanent protection:** quota reservation now returns an identity token and is released when partial-file creation fails. The cleanup preserves an already-existing partial file when exclusive creation fails with `EEXIST`. `BeesServer~/test/rlTelemetryUploads.module.test.js` covers quota rollback and preservation of the colliding file. The regression case was added but not run, per the static-only audit scope.  
**Verification:** traced begin admission from quota reservation through exclusive partial-file creation and confirmed session maps are populated only after successful allocation. The failure path releases quota in a `finally` and avoids unlinking a file it did not create. Source and guard were reread after editing. No tests or runtime checks were run.  
**Invariant/knowledge:** failures before session ownership is established must release reserved upload quota without disturbing pre-existing upload state.

### REG-071 — Draft recovery overwrote a known terminal telemetry result
**Area:** `Scripts/Scenes/RlLiveTelemetryRecorder.cs`, gameplay telemetry draft recovery and training labels  
**Symptom:** when finalization could not move a completed segment into pending storage, a later application restart recovered the draft as `timeout`, discarding a known `bee_win`, `human_win`, or `draw` result.  
**Root cause:** recovery unconditionally replaced every abandoned draft's result with `timeout`, and finalization did not persist its known result before attempting the pending write.  
**Permanent protection:** finalization writes an atomic sidecar result marker before writing the pending payload. Recovery preserves recognized terminal results from that marker and uses `timeout` for older drafts or invalid/missing markers. Failed live finalization remains retryable; marker data is not added to the uploaded payload. `RlLiveTelemetryTests.DraftRecoveryPreservesKnownTerminalResultsAndDefaultsUnknownResultsToTimeout` protects the result mapping.  
**Verification:** statically traced marker creation, pending-write failure, live retry, crash recovery, successful cleanup, and backward-compatible timeout fallback. The focused regression was added but not run; no tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** persist known terminal outcomes before a recoverable handoff; timeout is a fallback only when the terminal result is genuinely unknown.

### REG-072 — Live experience capture used world coordinates instead of the policy frame
**Area:** `Scripts/Scenes/RlOneVsOneAgent.cs`, `RlLivePolicyAgent.cs`, `RlLiveTelemetryRecorder.cs`, `RlGameplayDemonstrationAgent.cs`, frozen RL coordinate ABI  
**Symptom:** when the active level's policy frame was rotated, live inference consumed rotated observations and mapped actions through that frame, while telemetry and demonstrations recorded world-frame observations and movement/aim vectors. The stored observation/action pairs therefore described a different policy interface than the frozen model used.  
**Root cause:** live policy and training agents resolved the per-team episode frame, but the two gameplay capture paths hard-coded frame zero and left recorded vectors untransformed.  
**Permanent protection:** `RlOneVsOneAgent.GetPolicyFrameQuarterTurns` is the shared resolver used by live inference, telemetry, and demonstration capture. Capture paths transform observations, movement, and weapon aim with the corresponding frame. `RlLiveTelemetryTests.GameplayTelemetryUsesTheInferencePolicyCoordinateFrame` and `RlGameplayDemonstrationAgentTests.DemonstrationsUseTheInferencePolicyCoordinateFrame` guard both producers.  
**Verification:** statically traced training and live inference observation/action transforms against both capture paths, then reread the shared resolver and changed producers. Focused regression guards were added but not run; no tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** every example stored under the policy ABI must encode observations and directional actions in the same coordinate frame used by that policy.

### REG-073 — Live telemetry zeroed deployed policy communication actions
**Area:** `Scripts/Scenes/RlOneVsOneAgent.cs`, `Scripts/Scenes/RlLiveTelemetryRecorder.cs`, live telemetry continuous action ABI  
**Symptom:** gameplay telemetry from deployed neural controllers recorded four zero communication outputs even when the policy had issued non-zero private allied communication values. Those rows retained a neural controller label but not the action the model produced.  
**Root cause:** the recorder populated movement and turret actions but did not copy the active policy's stored communication vector into continuous action slots 12–15.  
**Permanent protection:** `RlOneVsOneAgent.GetCommunicationActions` exposes the current per-ship vector with a zero default; telemetry copies all four values for neural-controlled ships and leaves external human/Hive Mind channels neutral. `RlLiveTelemetryTests.LiveTelemetryPreservesNeuralCommunicationActions` guards the accessor and recording path.  
**Verification:** traced action output through `SetCommunicationActions`, the per-ship communication store, and ally observation encoding, then reread the telemetry branch that copies the current vector. The focused regression guard was added but not run; no tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** telemetry labeled as the action actually taken must preserve every continuous action channel emitted by the deployed policy.

### REG-074 — Public demo staging did not preserve validated quarantine hashes
**Area:** `Training/bees_continual_public_demo.py`, authenticated public demonstration handoff  
**Symptom:** the importer verified the quarantined demo and manifest hashes, then copied those files into a temporary staging directory for native parsing without checking that the staged bytes still matched the authenticated hashes. A file change during the validate-to-copy window could cause bytes other than the validated upload to be imported while provenance reported the original quarantine hashes.  
**Root cause:** the staging handoff trusted `shutil.copy2` to preserve the source contents and did not bind the staged inputs to the hashes returned by quarantine validation.  
**Permanent protection:** the importer now hashes both staged files after copying and rejects either mismatch before calling the native demonstration importer. `PublicDemoQuarantineTests.test_staged_demo_tampering_after_validation_is_rejected` changes the staged demo after the copy and requires rejection before a batch is stored.  
**Verification:** statically traced quarantine hash validation, copy, staged-hash checks, native import, and provenance creation. The focused regression guard was added but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** every file passed to native parsing must be byte-identical to the authenticated quarantine payload whose hashes are recorded as provenance.

### REG-075 — Native demo hash was recorded after parsing
**Area:** `Training/bees_continual_native_demo.py`, native demonstration parser input integrity  
**Symptom:** native demonstrations were parsed before the importer computed the source SHA-256. A persistent file change during parsing could therefore make the importer archive and identify bytes that were not present when parsing began, without detecting the change.  
**Root cause:** the importer established content identity after invoking the path-based parser rather than hashing the source before parsing and verifying it afterward.  
**Permanent protection:** the importer now captures the source hash before invoking the native loader and rejects a changed source immediately after parsing; its existing post-archive check remains in place. `NativeDemoIngestionTests.test_demo_changes_during_native_parsing_are_rejected` mutates the source from the loader and requires rejection before a batch is stored.  
**Verification:** statically traced source validation, pre-parse hash, loader call, post-parse hash, archive copy, and post-archive hash check. The focused regression guard was added but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** content identity must be captured before parsing and remain stable through parsing and archival.

### REG-076 — WAN actors labeled fetched policy bytes with an earlier state version
**Area:** `Training/bees_wan_actor_worker.py`, WAN inference policy synchronization  
**Symptom:** an actor read policy version N from central state, then requested the latest policy payload independently. If the learner published N+1 between those requests, the actor loaded N+1 weights but labeled resulting trajectories as N. The learner rejects that stale version, so a normal publish race could discard rollouts and force actor resynchronization.  
**Root cause:** the actor discarded the policy response's `X-Bees-Policy-Version` header and recorded the earlier state version as the version actually loaded.  
**Permanent protection:** the policy client now returns the response version with the payload. Actor synchronization records the version attached to the exact fetched payload, while preserving the legacy payload-only client method. `ActorPolicyVersionTests.test_applied_snapshot_version_is_not_taken_from_older_state` protects the attribution.  
**Verification:** statically traced state version retrieval, policy endpoint snapshot/header construction, actor policy application, trajectory version labels, and learner-side stale-version rejection. The focused regression guard was added but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** a trajectory's policy version must identify the exact inference snapshot that generated it; stale and current policy metadata cannot be interchanged.


### REG-077 — WAN capacity diagnostics retained a stale trainer rate
**Area:** `Training/bees_elastic_wan_training.py`, elastic WAN capacity measurement  
**Symptom:** after trainer progress stopped, the capacity monitor could keep reporting the rate from its last two step samples indefinitely. Repeated observations of an unchanged step do not add samples, and pruning deliberately retains two samples even when they are both outside the measurement window. Capacity comparisons could therefore use a stale throughput baseline.  
**Root cause:** `trainer_rate()` checked sample count and elapsed time but did not require the newest sample to remain within the configured measurement window.  
**Permanent protection:** the diagnostic now returns no rate when its latest progress sample is older than the measurement window. `CapacityDiagnosticTests.test_trainer_rate_expires_when_progress_samples_are_stale` protects this case.  
**Verification:** statically traced trainer-step observation, sample pruning, rate calculation, and capacity reporting. The focused regression guard was added but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** a reported training rate must be based on step progress observed inside its declared time window.

### REG-078 — Managed log uploads appended a replacement file after an old cursor
**Area:** `Training/bees_training_worker_agent.py`, run-scoped diagnostic log upload  
**Symptom:** if a log path was replaced by a new file whose size already exceeded the uploader's previous cursor, the uploader treated the cursor as belonging to the replacement and appended only its suffix to the server's old file, losing the replacement's prefix.  
**Root cause:** rotation recovery compared file size with the cursor but did not track the identity of the file associated with that cursor.  
**Permanent protection:** the uploader records each path's device/inode identity and resets the server copy when that identity changes; the existing size-shrink recovery remains. `TrainingControlClientTests.test_training_log_uploader_resets_when_log_file_is_replaced` protects same-path replacement with a longer file.  
**Verification:** statically traced local file identity and cursor handling, reset requests, and the server's offset/reset contract. The focused regression guard was added but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** an upload cursor belongs to a specific log-file instance; a replacement file must start a new remote stream.

### REG-079 — Batched RL recovery reissued an action for an unprocessed worker response
**Area:** `Training/bees_mlagents_learn.py`, cross-worker Unity step recovery  
**Symptom:** if one worker exited while another worker's step response had already been consumed, the recovery path queued work for every idle worker. The completed worker was marked idle even though its response had not yet updated `previous_step`, so the same observation could receive another action before the first response was processed, misaligning environment actions and PPO experience.  
**Root cause:** the fast-step loop preserved completed responses but did not exclude those workers from the recovery call to `_queue_steps()`.  
**Permanent protection:** recovery temporarily marks workers with consumed responses busy while it queues restarted workers, then restores their idle state for normal postprocessing. `FastEnvManagerTests.test_worker_exit_does_not_discard_other_consumed_step_results` now asserts that recovery does not requeue a completed worker.  
**Verification:** statically traced initial action dispatch, response dequeue/state updates, worker restart, recovery dispatch, and trajectory postprocessing. The focused regression guard was updated but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** do not issue a second action from a worker's prior observation until its already-consumed response has been postprocessed.

### REG-080 — PPO compatibility patch changed continuous entropy scale under weapon masking
**Area:** `Training/bees_mlagents_ppo_compat.py`, inactive weapon-aim masking  
**Symptom:** PPO's continuous entropy contribution varied with the number of active weapon-aim dimensions, changing its scale relative to the discrete exploration terms for ships with different weapon counts.  
**Root cause:** the compatibility patch summed active Gaussian entropy dimensions. The pinned ML-Agents 1.1.0 `GaussianDistInstance.entropy()` averages entropy over dimension 1; replacing inactive dimensions with a sum changed that contract.  
**Permanent protection:** the patch now computes the mean Gaussian entropy over active continuous dimensions only, retaining ML-Agents' scale when all dimensions are active while excluding nonexistent aim slots. `InactiveContinuousActionMaskTests.test_masked_entropy_averages_only_active_continuous_dimensions` protects this behavior.  
**Verification:** compared the custom calculation with the pinned `release_22` source, whose trainer package declares version 1.1.0, and traced how `ActionModel` adds continuous entropy to per-branch discrete entropy. The focused regression guard was updated but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** masking inactive continuous dimensions must average over the remaining active Gaussian dimensions to preserve ML-Agents' continuous entropy scale.

### REG-081 — Legacy Pluto IV could omit the terminal evacuation interval from its score
**Area:** `Scripts/Levels/Level.Campaign.Pluto.cs`, legacy Pluto IV evacuation objective accounting  
**Symptom:** in the legacy `Pluto4BluerPastures()` path, when the timer callback first ran after the 300.49-second evacuation deadline, the mission ended before refreshing `personnelEvacuated`. The last completed five-second interval could be omitted from `_questPoints` and the fleet reward tier. The catalog-selected campaign implementation already refreshes the count before checking terminal conditions.  
**Root cause:** the terminal time/health check preceded the interval-count calculation, which ran only on nonterminal ticks.  
**Permanent protection:** the timer now refreshes the clamped evacuation count before either terminal condition, then records that current count as the score. `CampaignResourceAccountingTests.LegacyPlutoEvacuationScoreIncludesTheTerminalTimerTick` guards this ordering.  
**Verification:** statically traced the timer callback, deadline condition, `_questPoints` assignment, and ending reward tiers. The focused regression guard was added but not run; no tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** terminal mission scoring must include all completed objective intervals observable on the terminal timer tick.

### REG-082 — RL evaluation randomized ship identity codes outside the seeded scenario stream
**Area:** `Scripts/Scenes/RlEpisodeShipIdentity.cs`, `Scripts/Scenes/RlOneVsOneScenarioSeed.cs`, deterministic RL evaluation  
**Symptom:** repeated evaluation runs with the same ML-Agents seed could present different per-ship identity observations, adding uncontrolled observation variation to the measured policy result.  
**Root cause:** the identity permutation RNG used a fresh GUID even though evaluation scenario randomness is derived from the seeded Unity root and the identity stream already has its own `IdentityStreamSalt`.  
**Permanent protection:** ship identity permutations now use `RlOneVsOneScenarioSeed.Create(level, IdentityStreamSalt)`, keeping the identity stream isolated while making its sequence reproducible for a given evaluation seed and arena. `RlScenarioSeedTests.RuntimeScenarioSamplersUseMlAgentsRootAndStableArenaStreams` guards this source contract.  
**Verification:** statically traced evaluation root-seed initialization, per-arena/per-stream derivation, and identity permutation creation. The focused regression guard was added but not run; no tests or runtime checks were performed, per the static-only audit scope.  
**Invariant/knowledge:** every stochastic input to an authoritative seeded evaluation must derive from a private arena/stream seed rather than unseeded process randomness.

### REG-083 — RL evaluation randomized policy coordinate frames outside the seeded scenario stream
**Area:** `Scripts/Scenes/RlOneVsOneAgent.cs`, `Scripts/Scenes/RlOneVsOneScenarioSeed.cs`, deterministic RL evaluation  
**Symptom:** repeated evaluation runs with the same ML-Agents seed could rotate team observations and actions differently, adding uncontrolled directional variation to policy results.  
**Root cause:** the per-arena coordinate-frame sampler used a process-wide RNG seeded from a fresh GUID, despite authoritative evaluation using a seeded Unity root and the scenario seed system providing isolated streams by arena.  
**Permanent protection:** each arena now owns a coordinate-frame RNG derived from `CoordinateFrameStreamSalt`; it advances across that arena's episodes while remaining independent of other arenas. Scene-load and test resets clear both cached frames and RNGs. `RlScenarioSeedTests.RuntimeScenarioSamplersUseMlAgentsRootAndStableArenaStreams` requires this seeded stream and rejects the prior unseeded RNG; stream-derivation coverage also checks separation from matchup and map-size RNGs.  
**Verification:** statically traced evaluation root initialization, arena and stream seed derivation, frame creation and episode invalidation. Regression guards were updated but not run; no tests, builds, Unity, simulations, or runtime checks were run, per the static-only audit scope.  
**Invariant/knowledge:** every stochastic input that changes seeded evaluation observations or action transforms must use a private deterministic arena stream.

### REG-084 — PPO compatibility layout guard expected obsolete action dimensions
**Area:** `Training/bees_mlagents_ppo_compat_tests.py`, `Training/bees_mlagents_ppo_compat.py`, frozen RL action ABI  
**Symptom:** the PPO layout guard expected 12 continuous actions and nine discrete branches, including three 65-action branches. The compatibility implementation, training agent, and frozen policy schema v20 define 16 continuous actions and six discrete branches: five binary weapon-fire branches and one five-action special branch. Running the test would reject the current ABI before checking the compatibility behavior.  
**Root cause:** both expected dimensions were stale after the policy ABI added four communication actions and standardized the six-branch action layout; the assertion no longer matched production declarations or schema v20.  
**Permanent protection:** the independent layout assertion now requires 16 continuous actions and `(2, 2, 2, 2, 2, 5)` discrete branch sizes.  
**Verification:** compared the test against the compatibility module's action constants, `RlOneVsOneAgent.CreateDiscreteBranchSizes()`, and `RlPolicySchema`'s v20 contract. The corrected test was not run, per the static-only audit scope.  
**Invariant/knowledge:** PPO compatibility guards must assert every dimension of the frozen action ABI, including continuous communication channels, and must not retain obsolete slots.
### REG-085 — deferred campaign triggers survived a level rebuild
**Area:** `Scripts/Levels/Level.Campaign.Shared.cs`, campaign trigger graph lifecycle  
**Symptom:** a campaign level rebuilt on the same `Level` instance could retain triggers queued in `NextTriggers` from the prior mission. `ResetRuntimeState` cancels timers, then `SetTriggers` previously cleared only active triggers; the next trigger poll would append the stale deferred entries to the new mission.  
**Root cause:** trigger rebuild cleared the active queue but not the deferred queue.  
**Fix:** `SetTriggers` now clears both trigger collections before configuring the current mission.  
**Permanent protection:** `CampaignTriggerStructureTests.RebuildingCampaignTriggersDiscardsDeferredTriggersFromThePreviousLevel` guards both clears and their ordering. The regression test was not run, per the static-only audit scope.  
**Invariant/knowledge:** rebuilding a mission trigger graph must discard both active and deferred triggers from the previous level.
