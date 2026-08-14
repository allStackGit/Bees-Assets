# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

1. **PERF-057 — Fire Tank obstacle breakup creates and destroys cosmetic GameObject trees per fragment.** `Obstacle.BreakApart()` creates a new debris root for every fragment, and `ObstacleDebrisPiece.Awake()` creates an additional renderer child; the default Fire Tank breakup requests 24 fragments, then every root is destroyed after its short lifetime. Pool debris pieces at Stage scope so renderer objects are created only when pool capacity grows and pieces are reset/reused across explosions without changing deterministic breakup motion or rendering.
