# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-013 — Skip per-shot projectile naming during training
**Location:** `Scripts/Entities/Projectiles/Projectile.cs/Setup()`  
**Cost:** Every pooled projectile setup builds an interpolated string from shooter name, projectile type, and per-shot Id, then assigns both `Projectile.Name` and `gameObject.name`. Weapon fire can create many projectile setups per second, so Hive Mind training pays repeated managed string allocation plus a Unity object-name write even though the projectile object itself is pooled.  
**Optimization:** Preserve the descriptive per-shot naming in non-training play/debugging, but skip rebuilding/assigning the name when `Stage.IsTraining`; the stable prefab-era name set during `Create()` is sufficient there.  
**Evidence:** `Level.AddProjectile()` obtains a pooled projectile and calls `Setup()` on every shot; `Projectile.Setup()` unconditionally performs the interpolation and `gameObject.name` assignment. Active projectile collision/damage/movement logic uses Id, Type, Shooter, Target, Weapon and state references rather than `Name`; projectile-name references found in subclasses are commented diagnostic logs, and pool release dispatches on `Type`.  
**Risk:** Keep non-training/editor diagnostics unchanged. Training errors that stringify the projectile may show the stable prefab name rather than a per-shot descriptive name, but gameplay identity and attribution must continue to use `Id`/command outcome fields exactly as before.

Clean static passes: 0 / 2.
