# Combat / Targeting / Visibility Route

Load this route only when a focused combat task needs ownership or cross-system context beyond the exact source/tests.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| combat damage, TSV, delayed projectile attribution | `Ship.Combat`, `LogAttackingDamage`, `CreditAttackerCommandTsv`, `StoredCommand` | projectile may outlive firing command; originating `OutcomeId` owns attribution until flush |
| targeting / shooting / weapon range | `Weapon`, `RangeCollider`, `ShipsWithinRange`, `MakeSortedTargetingList`, `ShipDamageStatus` | enemy-only physics cache; reverse-range ownership; reserved incoming damage |
| line of fire / coordinates | `Weapon.HasClearLineOfFire`, `Physics2D.Linecast`, `Entity.GetPosition` | world-space physics vs Level-local gameplay/pathfinding coordinates |
| map-object visibility / multiple observers | `RangeCollider`, `MapObjectVisibilityTracker`, `PlayerVisibleMapObjects` | contact counts and source ownership; one observer exit must not erase another |
| Fire Tank / canister / neutral hazard damage | `CanisterBomb`, `Obstacle.BreakApart`, Fire Tank prefab | friendly/neutral damage rules plus pathfinder dirtying after obstacle breakup |

Verify damage semantics against current ship/projectile configuration and focused combat tests; do not infer capabilities from names alone.
