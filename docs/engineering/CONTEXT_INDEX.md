# Bees Context Router

Secondary router for ambiguous or cross-cutting work. This file is **not startup payload**: focused tasks should use the direct routes in root `AGENTS.md` and exact source/assets/tests first.

| Area / aliases | Detailed route | Start with current symbols / assets |
|---|---|---|
| RL, ML-Agents, training, continual learning, unified training, Hive Mind training | `context/RL.md` | `RlOneVsOne*`, `Training/`, `HiveMindTrainingBootstrap` |
| runtime, startup, identity, persistence, pooling, Level reset | `context/RUNTIME.md` | `ConfigData`, `Level`, `GameState`, `Ships`, `DataFile` |
| pathfinding, movement, obstacles, worker ownership, performance | `context/PATHFINDING.md` | `Pathfinder*`, `Ship.Movement`, obstacle code |
| combat, TSV, targeting, weapons, visibility | `context/COMBAT.md` | `Ship.Combat`, `Weapon`, `RangeCollider`, command/outcome code |
| sockets, server, reconnect, requests, WebGL, AOT | `context/NETWORKING.md` | `Socket`, request/response lifecycle, WebSocket bridge |
| UI, responsive layout, Squad Maker, viewport | `context/UI.md` | relevant layout guard/controller/prefab/scene |
| campaign, missions, maps, prefabs, Resources | `context/CAMPAIGN_ASSETS.md` | mission catalog/setup, exact map/prefab/resource |
| replay, tests, validation, agent learning, context | `context/ENGINEERING.md` | replay code, focused test, skill/guardrail being changed |

## Retrieval rules

- Search/fetch the exact symbol, asset, scene, prefab, configuration, test, or error term before broad scans when the owner is already obvious.
- Read only the detailed route(s) needed for the unresolved area; a row is a pointer, not another required reading list.
- For client/server wire, persistence, reconnect, learning-key, or identity contracts, inspect both repositories before changing the contract.
- For async/lifecycle bugs, trace both work ownership and publication ownership.
- Stop once the affected contract, current owner/symbols, important dependency, and validation evidence are known.
- Update stale routes when touched code/assets move. Maintained documentation is navigation, not authority; verify material behavior against current source/assets/tests.
