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
**Area:** `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, root screen-space canvases, large legacy `RectTransform` wrappers, dynamically instantiated UI canvases  
**Symptom:** UI that was correct at the historic 1366x768/16:9 authoring size remained misplaced or failed to use the available screen correctly on Macs and other displays with different resolutions/aspect ratios.  
**Root cause:** the first responsive repair recognized only exact reference-sized containers with children. It therefore skipped leaf backers and large screen-relative frames such as 1366x668 layouts, and scene-load-only installation missed root canvases instantiated later. Those objects remained attached to the obsolete reference rectangle even though the root `CanvasScaler` itself was resolution independent.  
**Permanent protection:** `Tests/EditMode/ResponsiveScreenLayoutGuardTests.cs` behaviorally exercises full-screen leaf panels, full-width/near-full-height frames with preserved margins, one-axis bars, and ordinary centered panels that must not be stretched. Late-created root canvases are discovered with the Unity-version-supported `Object.FindObjectsByType<Canvas>` API.  
**Verification:** run the `BeesFoundation` EditMode category to execute the geometry regressions, then perform a representative rendered PlayMode/player check at 16:9, 16:10, 3:2, 4:3 and ultrawide sizes (including the affected Mac display). Runtime execution is still required after this remote change.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` defines resolution/aspect-ratio independence, late-created root-canvas coverage, and the distinction between viewport-like wrappers and ordinary centered panels.

### REG-002 — Responsive UI fix referenced an unavailable Canvas API
**Area:** `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, `Tests/EditMode/ResponsiveScreenLayoutGuardTests.cs`, Unity API compatibility  
**Symptom:** the project failed compilation with `CS0117: 'Canvas' does not contain a definition for 'allCanvases'`.  
**Root cause:** the late-created-canvas discovery path was written against an assumed `Canvas.allCanvases` API without verifying that API against the repository's current Unity version. The source-level regression test repeated the same invalid assumption, so the impact/test review did not catch the compile contract.  
**Permanent protection:** the discovery path now uses Unity 6's supported `Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None)`. The focused source regression explicitly requires that API and rejects `Canvas.allCanvases`; normal Unity compilation remains the authoritative protection against unavailable engine APIs.  
**Verification:** compile/import the project in the documented Unity version (`6000.5.4f1`) before running the `BeesFoundation` EditMode category; then perform the rendered cross-resolution UI checks required by REG-001. This remote change has not itself been compiled or executed.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` requires new Unity engine APIs to be verified against the repository's documented Unity version before use.

### REG-003 — Responsive guards moved correctly authored HUD and Squad Maker UI
**Area:** `Scripts/UI Components/GameHudLayoutGuard.cs`, `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, gameplay HUD, Squad Maker  
**Symptom:** on the affected Mac/aspect ratios, squad-number tabs were pushed down from the top; the selected-squad action box and mini map were clipped below the bottom edge; Squad Maker level text moved below/through the correctly placed START/TEST buttons; and the Squad Maker dark footer disappeared below the viewport, exposing a white bottom strip and hiding BACK/START/TEST.  
**Root cause:** both responsive guards initially tried to translate ordinary fixed UI islands into a computed safe rectangle in addition to resizing legacy screen wrappers. That changed meaningful authored parent/sibling relationships and let independent guards fight over the same gameplay controls. The squad row also remained vulnerable to its `HorizontalLayoutGroup` reapplying legacy padding after per-child positioning. A later wrapper-only pass still rewrote the anchors of children already owned by a Unity `LayoutGroup`: in Squad Maker, the layout-managed 1366x718 `Main Container` and 1366x51 `Footer` are siblings that together form the authored vertical screen contract. Reanchoring the main child independently disrupted the layout pass and displaced the footer, exposing the white backing layer.  
**Permanent protection:** generic responsive repair is limited to viewport-like wrapper conversion and refuses to rewrite any child whose immediate parent is a Unity `LayoutGroup`; the layout system remains the geometry owner for those children. `ResponsiveScreenLayoutGuardTests.LayoutGroupOwnedScreenChildKeepsAuthoredLayoutContract` behaviorally models the 1366x718 failure and would have failed before this fix. `GameHudLayoutGuard` separately owns the explicit gameplay edge contracts: the Squad Tabs layout group is top-left with zero edge padding, the selected-squad action box is fully visible at bottom-left, and the mini map is fully visible at bottom-right. Existing source/behavior tests continue to reject generic safe-area island translation and gameplay-aware wrapper repair.  
**Verification:** compile/import, run the `BeesFoundation` EditMode category, then reproduce the supplied visual checks on the affected Mac: normal Pluto gameplay and pause (tabs/action box/mini map), Squad Maker (dark background/footer to the bottom edge, BACK at bottom-left, level details above bottom-right START/TEST, and no white strip), and a second non-16:9 aspect ratio. Rendered validation is required because final Unity layout-pass ordering cannot be proven by source inspection alone.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` separates generic viewport repair from semantic HUD placement and explicitly states that `LayoutGroup`-driven children remain under Unity layout ownership.

### REG-004 — Pluto IV shield/end-state HUD escaped its authored region
**Area:** `Scripts/Levels/Level.Campaign.Pluto4.cs`, `Scripts/UI Components/GameHudLayoutGuard.cs`, `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, Pluto IV shield/end-of-level HUD  
**Symptom:** the green planetary-shield health fill appeared outside/left of its rectangle, while Game Speed could overlap the shield UI during the Pluto IV timed/end-of-level state.  
**Root cause:** generic responsive island translation could move pieces of the authored shield hierarchy independently, producing the displaced fill. Separately, `GameHudLayoutGuard` derived the shield-state speed button x-position from the clock and overwrote Pluto IV's authored `-290` right inset. The shared Pluto/Titania health-bar control intentionally uses an established `0..150` scale convention; that convention is preserved rather than replaced by a Pluto-only `0..1` interpretation.  
**Permanent protection:** generic responsive repair no longer translates ordinary HUD islands. Pluto IV clamps the health fraction before applying the shared `fraction * 150` convention, while the HUD guard preserves the mission-specific `290`-pixel right inset whenever the Pluto shield is active and retains the existing counter/shield vertical rules. `GameHudLayoutGuardTests` protects both Pluto contracts, while the existing Titania UI regression continues to protect the shared scale convention.  
**Verification:** compile/import, run the `BeesFoundation` EditMode category, then play Pluto IV through the timed shield phase and end-of-level popup while observing full, partial and depleted shield states. Confirm the green fill remains within its rectangle and Game Speed does not overlap the shield/counter UI. Also verify Titania II's shield bar still fills correctly. This rendered mission-state check remains required after the remote change.  
**Invariant/knowledge:** the UI ownership rules in `docs/engineering/INVARIANTS.md` apply: mission-specific semantic placement must not be overwritten by generic responsive logic.
