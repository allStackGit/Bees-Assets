# UI / Squad Maker / Viewport Route

Load this route only when a focused UI task needs ownership/background beyond the exact guard/controller/prefab/scene and focused tests.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| WebGL outer margins / browser viewport | `WebGlResponsiveViewportBuildGuard`, generated WebGL `index.html`, `#unity-container`, `#unity-canvas` | diagnose browser viewport -> WebGL canvas before Unity UI; do not route normal Editor/standalone aspect bugs here |
| regular responsive UI / Squad Maker | `SquadMakerResponsiveLayoutGuard`, `SquadMakerCompositionLayoutGuard`, `SquadMakerHeaderBackdropGuard` | relevant prefab/scene plus focused regression test; derive geometry from immutable authored/reference state |
| placement / drag-drop / formations | `SquadMakerDragWorkspace`, `SquadMakerDragWorkspaceResizeGuard`, `Dropper`, `DragIcon` | fixed 600x340 logical placement canvas; resize is presentation-only and must not change saved offsets |
| dynamic fleet/squad rows | row creation/layout code in `SquadMaker` and prefab | preserve authored icon position; live vertical layout owns row width |
| color picker | `ColorPicker`, live COLOR button, root CanvasScaler | follow button; flip/clamp to root canvas; sample using rendered scale |
| level details / bottom controls | `SquadMaker.ToggleLevelOptions`, `ToggleLevelDetails`, `SquadMakerLevelDetailsFitGuard` | details get remaining chosen-column height; Supply Capacity/START/TEST remain visible |
| HUD / legacy screen layout | exact relevant guard and scene/prefab | validate the specific mission/screen and representative aspect ratios |

If a failure matches a known regression, read only the relevant entry in `docs/engineering/REGRESSIONS.md`, not the entire regression history.
