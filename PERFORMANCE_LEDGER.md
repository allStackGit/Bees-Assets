# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-001 — Remove LINQ from recurring Squad aggregate queries
**Location:** `Scripts/Levels/Squad.cs`, aggregate properties such as `MaxRange`, `SlowestSpeed`, `IsDefenseless`, `HasReachedDestination`, `HasDestination`, and related `Sum`/`Max`/`Min`/`All`/`Any` properties  
**Cost:** These properties dispatch through `System.Linq.Enumerable` over the squad ship list. Several are read from recurring command/combat timers, including sub-second attack loops, so every read performs another full ship scan with LINQ iterator/interface/delegate overhead; multiple properties can rescan the same squad within one command tick. The cost scales with active squad count and ships per squad.  
**Optimization:** Replace the hot aggregate properties with allocation-free/direct indexed loops (or narrowly cache only values with explicit safe invalidation). Preserve the existing empty-list semantics for `Sum`, `All`, `Any`, `Max`, and `Min`, and avoid caching health/range/speed state that can change independently of squad membership unless invalidation is proven complete.  
**Evidence:** Current `Squad.cs` defines these aggregates with `GetShips().Sum/Max/Min/All/Any`; current recurring commands repeatedly query `HasReachedDestination`, `MaxRange`, `IsDefenseless`, and related properties while deciding movement/combat behavior. This is reachable gameplay/training code and is multiplied across active squads.  
**Risk:** Direct-loop replacements must exactly preserve empty-squad behavior and dynamic ship-state semantics; incorrect caching or different empty behavior could change command decisions or hide invalid squad lifecycle states.

Clean static passes: 0 / 2.
