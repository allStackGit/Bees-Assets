# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

1. **PERF-046 — Hive Mind matchup selection materializes visible squads for every decision.** `Scripts/Levels/Commands/MatchupStrategy.cs/SortSquads()` calls `GameState.GetSquadsVisibleToHiveMind()`, which allocates a list and LINQ `Select`/`Where`/`Distinct` pipeline even though matchup selection immediately reduces the candidates to one squad. Reuse matchup-owned visible-squad list/deduplication buffers while preserving the player-side all-enemy-squads behavior.
