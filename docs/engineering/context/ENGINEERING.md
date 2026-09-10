# Replay / Testing / Agent Workflow Route

Load this route only when a focused task needs engineering-process or replay context beyond the exact implementation/test/skill being changed.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| replay / determinism / ordering | replay recorder/player and stable-ID/random-scope code | unordered collections; cosmetic random isolation; same-step ordering |
| test runner / release gate | affected test first; `docs/TESTING.md` for runner mechanics | authoritative XML/executed count; PlayMode for frame/physics/scene/real-worker behavior |
| validation requirements | root `AGENTS.md`; relevant section of `VALIDATION_POLICY.md` only if needed | proportional evidence; old logs never validate changed source |
| agent learning / retrieval | root `AGENTS.md`, exact skill/guardrail | `.agents/skills/{repo-learning,continuous-learning,search-index,code-quality}` are on-demand; `LEARNING_STATE.md` is for unresolved misses |
| regression knowledge | exact regression ID if implicated | `docs/engineering/REGRESSIONS.md`; do not preload full history |

A workflow-only change should stay in workflow docs/skills and their guardrail tests. Do not load gameplay architecture merely because it exists.
