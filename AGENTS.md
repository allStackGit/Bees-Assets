# AGENTS.md

Mandatory rules for coding/development work in this repository.

## Start here

- Follow the user's request and explicit branch target. Otherwise, do not make ordinary development changes directly on `main`.
- This root `AGENTS.md` is the only unconditional repository read. Inspect the exact relevant source/config/test next; load additional docs or history only when a concrete dependency, ambiguity, failed hypothesis, or cross-boundary risk requires them.
- For work under `BeesServer~/`, read and follow `BeesServer~/AGENTS.md` before changing that subtree.
- Reconcile the current branch/head and relevant intervening changes before continuing prior work. Do not rely on remembered repository state.
- Stop loading context once the affected contract, root cause/intent, important dependencies, and useful validation evidence are understood.

### Protected paths

- Never read or ingest `results/`, `.results/`, or `Demonstrations/` unless the user explicitly requests that protected path or a specific file within it.
- Without that explicit request, do not retrieve their contents directly or indirectly through broad search, history, diffs, indexing, or tooling that may surface excerpts.

## Engineering contract

Optimize for a working, understandable system rather than documentation volume, test count, or process ceremony.

- Reproduce reported failures before changing code when practical, and trace the causal chain before patching symptoms.
- Exercise the changed executable path whenever practical. Static/source-text/contract tests are supporting evidence, not proof that runtime behavior works.
- If a test cannot be run in the available environment, do not spend substantial effort adding it unless it protects a clear durable contract or known regression.
- Prefer simplifying states, components, ownership, and recovery paths over adding another watchdog, retry loop, flag, compatibility layer, or special case.
- When repeated local patches expose new failures, stop patching locally and reassess the design/root cause.
- Make coherent changes small enough to reason about and validate, without adding artificial ceremony or unnecessary delay.
- Write or expand documentation only for a concrete maintainer/user purpose, a demonstrated recurring mistake, or an explicit request. Keep it short and current.
- Maintain independent technical judgment. Re-check evidence when challenged; do not reverse a conclusion merely because the user disagrees. If it changes, identify what evidence or reasoning changed it.
- A passing qualification/unit/static suite proves only what it actually exercises. Do not infer live-system stability, autonomous recovery, deployment readiness, or multi-machine correctness from narrower evidence.
- Keep normal user-facing responses concise. Do not bury weak evidence or unresolved uncertainty in long explanations.

## Change and validation rules

- Before behavior changes, understand the relevant contracts, dependencies, ownership/lifecycle, and any persistence/network/UI/physics/performance implications that actually apply.
- Once the change is sufficiently understood, make it promptly. For multi-part work, preserve coherent completed changes rather than delaying everything for unrelated investigation.
- After each coherent edit, inspect the touched code/diff and run the strongest practical focused validation. Broaden only when risk or evidence warrants it.
- Never weaken, delete, or skip a valid test merely to pass. Add focused regression protection when it is useful and executable.
- Never claim old logs/results validate changed source. Do not use GitHub Actions for development, testing, patching, builds, qualification, or verification.
- If important validation cannot be executed, keep safe completed work, run what is available, and state exactly what remains unverified.

For substantive coding completion, report briefly:

**Changed:** what changed and why  
**Verified by:** what was actually executed or inspected  
**Observed result:** what the evidence showed  
**Not verified:** material validation not performed, if any
