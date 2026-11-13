# AGENTS.md

Mandatory rules for coding/development work in this repository.

## Start here

- Follow the user's request and explicit branch target. Otherwise, do not make ordinary development changes directly on `main`.
- This root `AGENTS.md` is the only unconditional repository read. Inspect the exact relevant source/config/test next; load additional docs or history only when a concrete dependency, ambiguity, failed hypothesis, or cross-boundary risk requires them.
- Reconcile the current branch/head and relevant intervening changes before continuing prior work. Do not rely on remembered repository state.
- Stop loading context once the affected contract, root cause/intent, important dependencies, and useful validation evidence are understood.

### Protected paths

- Never read or ingest `results/`, `.results/`, `Demonstrations/`, `Logs/`, or `TrainingHistory~/` unless the user explicitly requests that protected path or a specific file within it.

## Engineering contract

DO NOT UNDER ANY CIRCUMSTANCES MAKE OR SUGGEST CHANGES WHEN YOU DON'T UNDERSTAND THE SYSTEM.

Optimize for a working, understandable system rather than documentation volume, test count, or process ceremony.

- Reproduce reported failures before changing code when practical, and trace the causal chain before patching symptoms.
- Do not add regression tests for bug fixes or previously observed failures. Do not propose, recommend, request, or leave TODO/comments suggesting regression tests unless the user explicitly asks for them.
- Prefer simplifying states, components, ownership, and recovery paths over adding another watchdog, retry loop, flag, compatibility layer, or special case.
- Write or expand documentation only for a concrete maintainer/user purpose, a demonstrated recurring mistake, or an explicit request. Keep it short and current.
- Maintain independent technical judgment. Re-check evidence when challenged; do not reverse a conclusion merely because the user disagrees. If it changes, identify what evidence or reasoning changed it.
- Keep normal user-facing responses concise. Do not bury weak evidence or unresolved uncertainty in long explanations.


## Change and validation rules

- Before behavior changes, understand the relevant contracts, dependencies, ownership/lifecycle, and any persistence/network/UI/physics/performance implications that actually apply.
- Once the change is sufficiently understood, make it promptly. For multi-part work, preserve coherent completed changes rather than delaying everything for unrelated investigation.
- Do not use GitHub Actions for development, testing, patching, builds, qualification, or verification.


For substantive coding completion, report briefly:

**Changed:** what changed and why  
**Observed result:** what the evidence showed
