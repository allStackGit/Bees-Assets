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
