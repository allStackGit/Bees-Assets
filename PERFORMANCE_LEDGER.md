# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

## PERF-033 — Avoid idle turret target scans before rotation guard

- **Location:** `Scripts/Entities/Ships/Weapons/Turret.Aiming.cs::Aim`
- **Evidence:** The idle/default branch evaluates `!HasValidTarget()` before checking `Rotation != Ship.Rotation`. `HasValidTarget()` iterates `ShipsWithinRange` and calls `IsShipValidTarget`, which can include obstacle line-of-sight checks. This work is unnecessary when the turret is already aligned and no rotation correction can occur.
- **Intended change:** Check `Rotation != Ship.Rotation` first, then evaluate cease-fire/target validity only when a rotation correction is actually needed. This preserves the existing rotation decision while removing repeated idle fixed-update scans.

Clean static passes: 0 / 2.
