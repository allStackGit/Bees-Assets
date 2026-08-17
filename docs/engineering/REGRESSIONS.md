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

### REG-005 — Nested legacy menu layouts left gutters on wide and tall screens
**Area:** `Scripts/UI Components/LegacyScreenResponsiveLayoutGuard.cs`, Main Menu, Squad Maker, nested `HorizontalLayoutGroup`/`VerticalLayoutGroup` regions  
**Symptom:** Squad Maker exposed large blue horizontal or vertical gutters between its major regions at non-16:9 sizes, while Main Menu remained inside a legacy aspect-shaped interactive frame and left large unused bands on wide or tall displays.  
**Root cause:** root-canvas compatibility correctly expanded viewport owners, but responsive ownership stopped at those owners. Nested structural layout groups could receive a larger region from an expanded parent while retaining reference-sized cross-axis children. Main Menu also has a single direct canvas branch owning its interactive controls, so preserving that branch's legacy fixed frame produced whole-menu letterboxing even though the full root canvas was available.  
**Permanent protection:** `LegacyScreenResponsiveLayoutGuard` is restricted to Main Menu and Squad Maker. It reuses the existing dominant-axis/cross-axis sizing rules for nested layout groups that occupy a material fraction of the screen, while filtering out small local button rows. On Main Menu it expands only the direct canvas branch that owns all menu `Selectable`s, leaving background siblings untouched. `LegacyScreenResponsiveLayoutGuardTests` behaviorally protects interactive-root expansion, nested structural cross-axis fill, and the small-local-row exclusion.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render Main Menu and Squad Maker at 16:9, 16:10, 3:2, 4:3, portrait/tall and ultrawide sizes. Confirm that major UI regions use the available screen without blue/blank gutters, that Main Menu background presentation remains intact, and that local button rows retain their authored sizing. Remote edits still require this Unity execution and rendered validation.  
**Invariant/knowledge:** responsive ownership is hierarchical: expanding a root viewport does not by itself make nested structural layouts responsive. Screen-scale structural layout groups may need a second sizing pass, but local controls must remain outside that repair scope.

### REG-006 — Squad Maker hover descriptions displaced campaign level details
**Area:** `Scripts/UI Components/SquadMakerResponsiveLayoutGuard.cs`, Squad Maker Chosen Squads column, START/TEST hover descriptions  
**Symptom:** at QHD the Squad Maker showed the Chosen Squads column and START/TEST controls but the campaign level title/details were missing from the right side.  
**Root cause:** the hover-description stabilization guard assumed START and TEST were mutually exclusive and kept the active button's invisible description GameObject participating in the right-column `LayoutGroup`. The Squad Maker can expose START and TEST simultaneously, so both invisible descriptions could remain active structural children and consume vertical layout space needed by the campaign level title/details.  
**Permanent protection:** `SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility` keeps hover descriptions active for stable pointer behavior but gives each a `LayoutElement` with `ignoreLayout = true`, making the descriptions visual overlays rather than structural rows. `Tests/EditMode/SquadMakerResponsiveLayoutGuardTests.cs` activates both descriptions simultaneously and verifies that both remain outside layout measurement while CanvasGroup hover visibility changes independently.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render the campaign Squad Maker at QHD and representative non-16:9 sizes. Confirm the level title/details are visible, START and TEST remain correctly positioned, and hovering either button shows its description without moving the column. This remote edit still requires Unity execution and rendered validation.  
**Invariant/knowledge:** START and TEST availability is not mutually exclusive. Their hover-only descriptions are visual overlays and must never reserve structural space in the Chosen Squads layout.
