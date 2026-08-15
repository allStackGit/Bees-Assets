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

### REG-003 — Non-16:9 UI edge ownership remained incomplete
**Area:** `Scripts/UI Components/GameHudLayoutGuard.cs`, `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, `Scripts/UI Components/RootCanvasCompatibilityGuard.cs`, gameplay HUD, Squad Maker, Level Intro and other root-canvas screens  
**Symptom:** repeated testing on the affected Mac still showed clipped edge HUD, clipped bottom controls and large white bottom strips. A follow-up attempt moved squad numbers, but put them in the wrong place; the required location is immediately to the right of the scoreboard when the scoreboard is visible. Earlier Windows/other-resolution screenshots could look correct while the same branch remained wrong on the Mac.  
**Root cause:** three separate ownership mistakes were being treated as one generic geometry problem. First, the authored `Space` scene deliberately gives `Squad Tabs` 200 px of left padding because the scoreboard occupies the top-left; the earlier runtime normalization erased that reservation with zero padding, and pinning the giant screen-sized Squad Tabs container to a corner could not reconstruct the semantic relationship. Second, several gameplay HUD controls are authored exactly on the canvas boundary; mathematically in-bounds is not enough when sliced borders/rendered edges can be visibly clipped at the platform window boundary. Third, stretching a viewport-level vertical `LayoutGroup` owner without resizing its dominant fixed-height body left the original body+footer height at the top of a taller logical canvas, exposing the white root backer below it.  
**Permanent protection:** `GameHudLayoutGuard` now makes the legacy Squad Tabs root fill the live canvas and derives the row's left padding from the rendered right edge of the active scoreboard plus a small gap; when no scoreboard is visible it uses a small top-left inset. Scoreboard, ordinary Game Speed, action-box and mini-map edge placement use a 10-unit rendering inset so their visible borders do not touch the exact canvas boundary. `RootCanvasCompatibilityGuard` no longer owns Squad Tabs. It repairs viewport-level layout ownership, assigns taller-display surplus to the dominant body of a full-screen `VerticalLayoutGroup` so a fixed footer remains at the real bottom, clamps direct root-canvas interactive islands that are genuinely outside the canvas, and gives explicit screen-edge navigation controls such as Back, Continue and Skip a 15-unit rendering inset. `GameHudLayoutGuardTests` behaviorally protects scoreboard-relative tab padding and gameplay edge inset geometry; `RootCanvasCompatibilityGuardTests` behaviorally protects taller-viewport body/footer fill, navigation-control inset geometry, and the absence of a second squad-tab owner.  
**Verification:** compile/import in Unity `6000.5.4f1`, run `BeesFoundation`, then repeat the supplied Mac checks: gameplay scoreboard and Game Speed must be fully inset, squad numbers must start immediately to the scoreboard's right (or top-left inset when no scoreboard exists), selected-squad action box and mini map must be fully visible, Level Intro Back/Continue controls must have visible bottom/side margin, and Squad Maker/other menu screens must have no white bottom strip. Repeat at a normal 16:9 size to ensure the reference layout remains unchanged apart from the intentional small edge insets. Rendered Mac validation is still required for this revision; EditMode geometry alone cannot prove final platform rendering.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` and `docs/engineering/SYSTEM_MAP.md` now record scoreboard-relative squad-tab ownership, visible edge inset semantics, navigation-control margin semantics, and dominant-body surplus handling for viewport-level vertical layouts.

### REG-004 — Shared Pluto/Titania timed shield HUD drifted apart
**Area:** `Scripts/Levels/Level.Campaign.Pluto4.cs`, `Scripts/Levels/Level.Titania2Enhancements.cs`, `Scripts/UI Components/GameHudLayoutGuard.cs`, `Scripts/UI Components/ResponsiveScreenLayoutGuard.cs`, Pluto IV and Titania II timed shield HUD  
**Symptom:** the green planetary-shield health fill could escape its rectangle, and later Pluto IV displayed Game Speed far to the left of the otherwise compact Planetary Shield/timer/Evacuated cluster. Titania II uses the same shield and timer controls, so a Pluto-only placement repair could regress Titania II.  
**Root cause:** generic responsive island translation originally moved pieces of the authored shield hierarchy independently. A follow-up repair then preserved Pluto IV with an absolute `-290` Game Speed x-position. That fixed one authored-size arrangement but detached Game Speed from the live shield geometry, so later responsive scaling made the fixed inset visibly wrong. The shared Pluto/Titania health-bar control also intentionally uses an established `0..150` scale convention and must not be reinterpreted as a Pluto-only `0..1` transform.  
**Permanent protection:** generic responsive repair does not translate ordinary HUD islands. Pluto IV clamps the health fraction before applying the shared `fraction * 150` convention. `GameHudLayoutGuard` now derives Pluto IV Game Speed placement from the live Planetary Shield and Evacuated-counter geometry: it right-aligns with the shield and top-aligns with the counter, falling back below the shield when no counter is visible. Titania II separately right-aligns Game Speed below the clock with its intended gap. The guard contains no Pluto-specific absolute x inset, and restores ordinary authored Game Speed placement whenever the mission clock is hidden. `GameHudLayoutGuardTests.TimedShieldAlignmentMathPreservesReferenceEdgesAndGap` behaviorally protects the geometry helpers, while the Pluto and Titania source-wiring regressions protect the two mission states and the shared shield-scale convention.  
**Verification:** compile/import in Unity `6000.5.4f1`, run the `BeesFoundation` EditMode category, then render both missions at representative 16:9 and non-16:9 sizes. For Pluto IV, check the timed state with Planetary Shield, timer, Evacuated counter and Game Speed together, including full/partial/depleted shield states and the transition out of the clock state. For Titania II, check Planetary Shield, timer and Game Speed with the counter hidden, again through changing shield health and mission completion.  
**Invariant/knowledge:** `docs/engineering/INVARIANTS.md` records that Pluto IV and Titania II share the Planetary Shield/timer cluster, while Pluto IV additionally owns the Evacuated counter; layout changes to this cluster must validate both missions.
