# Runtime / Identity / Persistence Route

Load this route only when a focused task needs runtime ownership, identity, persistence, or pooling context beyond exact source/tests.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| bootstrap, global configuration, scene startup | `ConfigData`, `ConfigData.Runtime`, `Scene`, `Stage` | settings/user-data finalization; lazy socket; profile selection |
| identity namespaces | `FleetShip.Id`, `SavedSquad.Id`, `Squad.ItemId`, `Ship.Id`, request `Hash`, `OutcomeId` | distinguish persistent, pooled-lifetime, request, learning, and database identities |
| profile persistence / local-server routing | `DataFile`, `UserData`, `ConfigData.Runtime`, `Ships` | missing vs failed read; exact version; local/server/mirror routing |
| atomic campaign/profile checkpoint | `CampaignCheckpoint` | BeesServer `campaignCheckpoint.js`; seven profile documents; transactional boundary |
| Level reset, teardown, pooling | `Level.Reset`, `GameState`, `GameState.Registry`, `Pool`, `Setup`, `ClearData`, `Kill` | loaded flags; deferred release; request-history pruning; focused/soak tests |
| persistent fleet/squad to runtime objects | `Ships`, `FleetShip`, `SavedSquad`, `SquadShip`, `LevelConstructor`, `Squad`, `Ship.Lifecycle` | negative generated IDs; membership and loaded-state semantics |

For ownership/lifecycle or persistence-contract changes, consult the relevant `SYSTEM_MAP.md` / `INVARIANTS.md` section and verify against current code/tests.
