# AGENTS.md

Mandatory engineering rules for every coding agent and development task in this repository.

## Optional cold-start primer

`PROJECT_PRIMER.md` is deliberately excluded from normal required reading. Read it only when the user explicitly requests cold-start onboarding, broad re-orientation, or use of the primer. Do not preload or refresh it as part of routine development tasks. Routine learning belongs in the smaller owner documents and context index below; refresh the primer only during an explicit curation/onboarding task or when the user specifically requests it.

## Minimum bootstrap and context budget

1. Follow the user's explicit request and branch target.
2. After this file, the only unconditional repository read is `docs/engineering/CONTEXT_INDEX.md`.
3. Read `docs/engineering/CONTEXT_INDEX.md` as the compact router. Start with the row that best matches the task and inspect the named current source/assets/tests before expanding.
4. For a focused task, load only the relevant owner-document section(s), current implementation/assets, and focused evidence identified by the router. **Do not preload** `PROJECT_CONSTITUTION.md`, all of `SYSTEM_MAP.md`, all of `INVARIANTS.md`, `DEVELOPMENT_MEMORY.md`, validation/regression history, or the generic skill stack merely because they exist.
5. Read `PROJECT_CONSTITUTION.md` when the task can change gameplay/product meaning, persistence/network contracts, lifecycle/ownership semantics, evidence/validation rules, required quality, or another project-definition boundary.
6. Read the relevant sections of `SYSTEM_MAP.md` and/or `INVARIANTS.md` when the change crosses ownership/lifecycle boundaries, touches a high-risk system, or the context index routes there. Read a specialist skill only when the task actually invokes that procedure. Generic skills are reference procedures, not mandatory startup payload.
7. Broad audits, architecture work, unfamiliar cross-cutting changes, or unresolved focused investigations may deliberately widen to the full maintained model.

### Retrieval stop rule

Once you can name the affected contract, current symbols/assets, important caller/callee or owner boundary, and the evidence needed to validate the change, **stop loading context**. Expand only for a concrete unresolved dependency, contradiction, retrieval miss, or failed hypothesis.

For documentation/skill/agent-workflow-only changes, the normal fast path is: `AGENTS.md -> CONTEXT_INDEX.md -> affected docs/skills -> any guardrail tests that directly enforce them`. Do not load gameplay architecture simply because the repository contains it.

Maintained documentation is a source-grounded navigation model, not unquestioned truth. Revalidate material facts against current code, assets, configuration and tests when correctness depends on them; repair stale knowledge when found.

## Branch discipline

Respect an explicitly named branch. Otherwise, do not make ordinary development changes directly on `main`; create a descriptive task branch from the latest appropriate base. Specialist skills may impose stricter rules.

## Change safety

Before a behavior-affecting edit, trace the affected path far enough to understand the enduring contract, important callers/callees, state/lifecycle ownership, serialized assets, persistence/network boundaries, UI/frame/physics behavior, and performance implications that are actually relevant. A narrow symptom fix is incomplete if a known indirect dependency can invalidate it, but this is not permission for an unrelated repository-wide scan.

If implementation conflicts with the constitution or a confirmed invariant, implementation is presumed defective unless the project owner explicitly changes the requirement.

For every behavior-affecting change, classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**. Never delete, skip, weaken, loosen or rewrite a test merely to make a change pass. A reproducible regression should gain focused automated protection that would have caught it whenever practical; otherwise record the strongest repeatable protection in `docs/engineering/REGRESSIONS.md`.

When execution is available, widen evidence proportionally:

`focused reproducer -> affected subsystem/category -> broader correctness suite -> full local release gate -> representative PlayMode/play/system validation when risk warrants it`

Never claim old XML/logs validate changed source.

## Repository learning must make future work cheaper

Use `.agents/skills/search-index/SKILL.md`, `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, and `.agents/skills/code-quality/SKILL.md` when their procedures are needed; do not preload them all at task start.

- `CONTEXT_INDEX.md` is the primary reusable routing layer. Prefer improving a route over adding another mandatory document.
- Persist only verified knowledge that was expensive to rediscover, prevents a likely repeated mistake, or materially shortens future retrieval.
- A new durable note, index entry, or required read should have positive context ROI: it should remove more future searching/reading than it adds.
- Update existing owner documents rather than accumulating overlapping notebooks. Replace stale statements; Git history already stores chronology.
- Retrieval misses or repeated agent mistakes should trigger the smallest routing/skill/test guardrail repair that prevents recurrence.
- A task may legitimately produce no documentation change. Do not create filler learning artifacts.
- Code-quality review is scoped to touched code and the immediate interfaces needed to judge it; documentation-only tasks do not require a code-quality scan.

## Completion gate

Do not call a behavior-affecting task complete until applicable items are true: root cause/intent is understood; important indirect dependencies were checked; affected tests were classified and stale/missing coverage handled; validation reached the strongest practical level; no safety/test contract was weakened for convenience; touched code received proportionate quality review; any durable learning/retrieval miss was reconciled; and remaining uncertainty or unexecuted validation is reported.

The target is evidence that intended behavior works and that the next similar task requires **less** context, not more.