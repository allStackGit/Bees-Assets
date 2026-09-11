# AGENTS.md

Mandatory rules for every coding/development task in this repository.

## Bootstrap

1. Follow the user's request and branch target.
2. This root `AGENTS.md` is the only unconditional repository read. After this file, no other repository document is unconditional.
3. Focused task: inspect the exact named/current source, asset, config, or focused test first. Routes are hints, not required reads.
4. Read `docs/engineering/CONTEXT_INDEX.md` only when the subsystem is unclear, boundaries cross, direct lookup misses, or the task is a broad audit/architecture review.
5. `PROJECT_PRIMER.md` is opt-in for explicit cold-start onboarding/re-orientation only.

### Protected paths

- **Never read or ingest `results/` unless the user explicitly requests access to `results/` or to a specific file within it.** Keeping `results/` tracked in Git/GitHub does not grant permission to inspect it.
- Without that explicit request, do not open, fetch, search, grep, glob through, index, summarize, parse, diff, inspect, or otherwise retrieve file contents from `results/`, including through repository-wide searches or tooling that could return excerpts from that path.
- Avoid broad content-search operations that cannot reliably exclude `results/`. If a tool may surface `results/` content incidentally, use a narrower path/file lookup instead.
- References to `results/` filenames or paths that appear incidentally in Git metadata are not permission to retrieve their contents. The restriction applies across branches, commits, diffs, history, and local/worktree copies.
- Only the user's explicit request overrides this protection for the scope they requested; do not treat a previous request to inspect one result as ongoing permission for later tasks.

### Direct routes

- RL/training → `RlOneVsOne*`, `Training/`, focused tests; optional `docs/engineering/context/RL.md`. Unified/continual design → `Training/bees_continual_learning_rl_implementation.md`.
- Tests → affected source/test; `docs/TESTING.md` only for runner/gate mechanics.
- Runtime/persistence/pooling → `Level`, `GameState`, `ConfigData`, `Ships`, `DataFile`; optional `context/RUNTIME.md`.
- Pathfinding/performance → `Pathfinder*`, movement/obstacles; optional `context/PATHFINDING.md`.
- Combat/targeting → `Ship`, `Weapon`, `RangeCollider`; optional `context/COMBAT.md`.
- Network/WebGL → socket/request/bridge code; optional `context/NETWORKING.md`; inspect BeesServer before shared-contract changes.
- UI → exact guard/controller/prefab/scene; optional `context/UI.md`.
- Campaign/assets → exact mission/asset; optional `context/CAMPAIGN_ASSETS.md`.
- Replay/agent workflow → exact implementation/test/skill; optional `context/ENGINEERING.md`.

### Retrieval stop rule

Once the affected contract, current symbols/assets, important dependency/owner boundary, and validation evidence are known, **stop loading context**. If a safe requested change is sufficiently understood, do not delay the first write for optional documents, history, broad searches, unrelated tests, or generic skills. Expand only for a concrete unresolved dependency, contradiction, retrieval miss, failed hypothesis, or evidence produced by the change. Do not preload broad architecture, memory, regression history, or generic skills.

Read `PROJECT_CONSTITUTION.md` only for possible gameplay/product meaning, persistence/network contract, lifecycle/ownership, evidence-rule, or project-definition changes. Read relevant `SYSTEM_MAP.md` / `INVARIANTS.md` sections only for high-risk/cross-boundary work or a concrete routed need. Documentation is navigation, not authority; verify material facts against current source/assets/tests.

## Change rules

Respect an explicitly named branch; otherwise do not make ordinary development changes directly on `main`.

Before behavior changes, trace only relevant contracts, dependencies, ownership/lifecycle, serialized/persistence/network boundaries, UI/frame/physics behavior, and performance implications. Do not turn this into an unrelated scan.

### Execution and write priority

For requested repository changes, once the affected contract, root cause or intent, important dependencies, and a safe edit are sufficiently understood, **perform the edit promptly**. Do not postpone all writes until optional investigation, broad validation, or unrelated review is complete.

For multi-part work, complete and preserve coherent changes before moving to independent later parts when practical. Prefer durable partial completion over an all-or-nothing workflow that leaves understood work unwritten if the execution or tool session ends.

After each coherent edit, inspect the touched diff/code and run the strongest practical focused validation. Broaden validation only when risk or evidence warrants it. If a runner, build, environment, or other validation is unavailable, keep the safe completed change, perform the validation that is available, and disclose what remains unexecuted; do not spend the work window repeatedly chasing unavailable validation or leave an understood safe edit unwritten solely because broader validation cannot run.

Classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**. Never weaken/delete/skip a test merely to pass. Add focused regression protection when practical; otherwise use `docs/engineering/REGRESSIONS.md`.

Widen evidence proportionally:
`focused reproducer -> affected subsystem/category -> broader correctness suite -> full local release gate -> representative PlayMode/play/system validation when warranted`

Never claim old XML/logs validate changed source. **Do not use GitHub Actions for development, testing, patching, builds, qualification, or verification.**

## Learning and completion

Use `.agents/skills/search-index/SKILL.md`, `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/code-quality/SKILL.md`, and specialist skills only when needed. Persist only verified knowledge that prevents repeated mistakes or reduces future retrieval; prefer repairing routes/owner statements over mandatory reads. New durable guidance needs positive context ROI.

For behavior changes, require understood intent/root cause and important dependencies, test classification, strongest practical validation, no weakened safety/test contract, proportionate touched-code review, and disclosure of remaining uncertainty/unexecuted validation.
