# Experiment Hub implementation status

Last updated: 2026-11-12. Development branch: `feat/hub-resource-experiments-20261110`.
This is a compact continuation index; `Training/EXPERIMENT_HUB.md` describes the detailed operator contracts. Update this file when a **substantive capability** is completed or a real development gap is established; do not append per-commit activity or speculative TODOs.

## Scope boundaries

Complete the non-Bees native Experiment Hub control plane, scheduling, experiment workflows, UI, and safe checkpoint/evaluation/lifecycle coordination. **Excluded:** Bees/Unity native rewrite integration, automatic resource-scaling and hardware performance optimization (separate workstreams). Production service deployment, real heterogeneous-host runs and performance qualification are a **later validation phase**, not a reason to rewrite working implementations. GPU compute/VRAM quotas are not enforced by existing native backends; do not imply they are.

## Implemented in this branch (software capability, not production qualification)

- **Coordinator and native ownership:** standalone Linux/Windows-capable control plane, authenticated machine enrollment/heartbeat, fresh agent-instance and capacity receipts, machine-local systemd/Windows Job Object supervision, durable native dispatch journals, Stop/Inspect reconciliation, process/resource usage reporting, native GPU identity/probes and explicit GPU claims.
- **Configuration and scheduling:** revisioned saved project configurations, immutable queued jobs, resource/admission previews, multi-project CPU/GPU allocation, frozen independent sequences/trials, multi-stage checkpoint continuations, atomic 2–16-job mixed-project resource bundles. Bundle members receive **one atomic native Start authorization** and **one confirmed group shutdown**, plus an explicitly approved future group launch that waits for fresh machine/lease/native readiness. CPU-only launch approval can survive coordinator restart with separate consent; GPU approval cannot. All group operations preserve independent native ownership and release claims only after safe receipts. No simultaneous process-start guarantee or common learning protocol.
- **Unattended CPU operation:** explicitly armed native queue scheduler, project-fair scheduling, native admission and terminal lease cleanup, CPU-only opt-in restart/agent recovery after fresh native reconciliation. Uncertain or failed native owners require operator review; GPU scheduling is separately authorized.
- **Stopping, selection, and continuation:** single- or same-checkpoint multi-suite independent evaluation gates, checkpoint-preserving Stop and verified continuation eligibility, final checkpoint fingerprint/evaluation ownership, reinspection, scheduled competing-candidate ranking and model-pinned promotion for supported projects.
- **TinyBrains distributed path:** atomically reserved learner/remote-collector group, pinned tailnet/HMAC endpoints, verified on-policy collector barrier, explicit coordinated launch approval, native sidecar teardown/aborted-startup cleanup, frozen per-collector action weights and source-independent rollout metrics. Deliberately separate from ordinary resource bundles.
- **Operator surfaces:** Design, Train, Monitor, Compare, Machines, independent evaluation/ranking, candidate selection, grouped queue lifecycle controls, native diagnostics and exports; common authenticated agent protocol and shared loopback operator API on standalone and integrated coordinator entry points.

## Remaining substantive implementation decisions / work

1. **Unattended distributed TinyBrains continuation across coordinator restarts:** the independently approved learner/collector launch intentionally has a short process-local GPU permission, unlike durable CPU-only resource-bundle plans. Decide whether the trusted deployed group needs a bounded restart-reconciled plan, retaining the exact authenticated collector/learner/optimizer safety barrier. No speculative automatic retry.
2. **Cross-project production adapter/evaluator parity:** finish any concrete missing trusted adapter or evaluator functionality for the original supported non-Bees projects (TinyBrains and Courier) after inspecting their actual runtime contracts. The Hub already has generic configuration, metrics, evaluation and supervisor interfaces; avoid building parallel lifecycle paths.
3. **Operator workflow completeness:** identify any high-frequency scheduling, recovery, checkpoint or candidate-selection action still requiring raw API access when the UI is expected to cover it; close meaningful gaps end-to-end. Comprehensive polished CLI parity is not yet asserted.
4. **Failure/recovery ownership across long experiment chains:** inspect only concrete unresolved cases in automatic stage handoff, model promotion, native terminal release and continued experiments; preserve safe failure states instead of automatic retries based on missing telemetry.

## Deferred, not implementation blockers

- Real multi-machine TinyBrains PPO run/STOP/checkpoint/continuation and offline worker qualification; actual Windows CUDA and Linux systemd/ROCm service deployment, coexistence, independent evaluator verification and production browser flows.
- Hardware efficiency tuning, dynamic CPU/GPU scaling, enforced third-party GPU quotas and Bees native rewrite integration.
- No new regression tests for past failures without an explicit user request. Prefer inexpensive executable path checks while building.
