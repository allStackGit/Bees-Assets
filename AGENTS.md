# AGENTS.md

Mandatory rules for coding/development work in this repository.

## Start here

- Follow the user's request and explicit branch target. Otherwise, do not make ordinary development changes directly on `main`.
- This root `AGENTS.md` is the only unconditional repository read. Inspect the exact relevant source/config/test next; load additional docs or history only when a concrete dependency, ambiguity, failed hypothesis, or cross-boundary risk requires them.
- For work under `BeesServer~/`, read and follow `BeesServer~/AGENTS.md` before changing that subtree.
- Reconcile the current branch/head and relevant intervening changes before continuing prior work. Do not rely on remembered repository state.
- Stop loading context once the affected contract, root cause/intent, important dependencies, and useful validation evidence are understood.

### Protected paths

- Never read or ingest `results/`, `.results/`, `Demonstrations/`, `Logs/`, or `TrainingHistory~/` unless the user explicitly requests that protected path or a specific file within it.
- Without that explicit request, do not retrieve their contents directly or indirectly through broad search, history, diffs, indexing, or tooling that may surface excerpts.

## Engineering contract

DO NOT UNDER ANY CIRCUMSTANCES MAKE OR SUGGEST CHANGES WHEN YOU DON'T UNDERSTAND THE SYSTEM.

Optimize for a working, understandable system rather than documentation volume, test count, or process ceremony.

- Reproduce reported failures before changing code when practical, and trace the causal chain before patching symptoms.
- Exercise the changed executable path whenever practical. Static/source-text/contract tests are supporting evidence, not proof that runtime behavior works.
- Do not add regression tests for bug fixes or previously observed failures. Do not propose, recommend, request, or leave TODO/comments suggesting regression tests unless the user explicitly asks for them.
- When validating a bug fix, prefer exercising the changed executable path and existing relevant tests; do not create a new test whose purpose is to prevent recurrence of that specific bug.
- Prefer simplifying states, components, ownership, and recovery paths over adding another watchdog, retry loop, flag, compatibility layer, or special case.
- When repeated local patches expose new failures, stop patching locally and reassess the design/root cause.
- Make coherent changes small enough to reason about and validate, without adding artificial ceremony or unnecessary delay.
- Write or expand documentation only for a concrete maintainer/user purpose, a demonstrated recurring mistake, or an explicit request. Keep it short and current.
- Maintain independent technical judgment. Re-check evidence when challenged; do not reverse a conclusion merely because the user disagrees. If it changes, identify what evidence or reasoning changed it.
- A passing qualification/unit/static suite proves only what it actually exercises. Do not infer live-system stability, autonomous recovery, deployment readiness, or multi-machine correctness from narrower evidence.
- Keep normal user-facing responses concise. Do not bury weak evidence or unresolved uncertainty in long explanations.


## Runtime and infrastructure failure protocol

For distributed training, networking, process lifecycle, checkpointing, rollout, worker, learner, supervisor, status/telemetry, build/release, stop/restart, and diagnostic-bundle failures:

- Do not patch the first plausible cause. Trace the affected end-to-end path and identify the earliest failure point actually supported by evidence.
- Correlate all symptoms from the same run before treating them as independent bugs. Classify each as root cause, contributing cause, downstream symptom, or still unresolved.
- When a diagnostic bundle is provided, inspect the run as a whole for correlated failures before changing code; do not stop at the first obvious error.
- Prefer removing faulty complexity or fixing ownership/state/lifecycle design over adding retries, watchdogs, guards, exception swallowing, compatibility branches, or recovery logic around symptoms.
- Do not add regression tests, including source-coupled tests that mirror implementation. Do not suggest adding them unless the user explicitly requests regression tests. Existing automated checks may still be run when relevant.
- If the root cause is not established, continue investigating and report the unresolved hypotheses rather than presenting a speculative patch as the solution.
- Before handing a runtime/infrastructure change back for operator validation, state: what failed; the evidence for the diagnosed cause; what changed; what material behavior remains unverified because the live Unity/multi-machine environment is unavailable; and the specific next-run observation that would confirm or falsify the diagnosis.

## Change and validation rules

- Before behavior changes, understand the relevant contracts, dependencies, ownership/lifecycle, and any persistence/network/UI/physics/performance implications that actually apply.
- Once the change is sufficiently understood, make it promptly. For multi-part work, preserve coherent completed changes rather than delaying everything for unrelated investigation.
- After each coherent edit, inspect the touched code/diff and run the strongest practical focused validation. Broaden only when risk or evidence warrants it.
- Never weaken, delete, or skip a valid test merely to pass. Do not add focused regression protection for bug fixes unless the user explicitly requests it.
- Prefer executable behavior/contract tests over source-text assertions. Do not add tests that depend on exact implementation strings, function ordering, formatting, comments, or incidental call shapes unless that text itself is the public contract. Remove or replace brittle representation tests when they create maintenance noise without detecting product regressions.
- Never claim old logs/results validate changed source. Do not use GitHub Actions for development, testing, patching, builds, qualification, or verification.
- If important validation cannot be executed, keep safe completed work, run what is available, and state exactly what remains unverified.

For substantive coding completion, report briefly:

**Changed:** what changed and why  
**Verified by:** what was actually executed or inspected  
**Observed result:** what the evidence showed  
**Not verified:** material validation not performed, if any
