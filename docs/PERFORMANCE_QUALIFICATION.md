# Bees performance qualification

## Purpose

Performance is treated as a tested compatibility requirement, not an informal optimization goal. The fast foundation categories protect correctness; `BeesPerformanceQualification` contains repeatable workloads whose measurements can be compared across commits and hardware tiers.

## Current gate

`Assets/Tests/PlayMode/PerformanceQualificationTests.cs` constructs a 256x256 world (64x64 path grid), forces one pathfinding worker, completes 25 real background A* requests, and executes 10,000 real runtime-state resets. It asserts:

- map setup <= 1000 ms;
- request p95 <= 250 ms;
- every request completes and returns a route;
- 10,000 `GameState.ResetState` calls complete in <= 1500 ms and leave key state clean.

Validated on 2026-08-02: 2/2 tests passed. The XML result is `Logs/BeesPerformancePlayMode.xml`; exact measurements are emitted as `PERF` lines in the log.

This deliberately exercises the one-worker configuration. `Scene.Start` now clamps `ConfigData.MaxThreads` to at least one, preventing a single-logical-processor system from permanently queueing every path request.

## Interpretation limits

The current test is a regression gate for pathfinding, not a complete minimum-spec certification. It does not measure rendering, GPU time, dense dynamic obstacles, large battles, audio, UI, memory growth, garbage-collection spikes, server latency, or thermal throttling. The generous threshold reduces false failures on shared development machines; release budgets should be established from measurements on physical target devices.

## Required next workloads

1. Dense static/dynamic obstacle pathfinding with small and large clearances.
2. Representative battle sizes measuring fixed-update CPU, allocations, and worst-frame time.
3. Rendered GPU/UI profiles at supported resolutions and quality settings.
4. Headless simulation throughput for future data collection/training.
5. A 30-60 minute setup/combat/teardown soak tracking managed/native memory and pool baselines.
6. A minimum-spec hardware matrix recording CPU, GPU, RAM, resolution, build, median, p95, p99, and maximum frame time.

Never raise a budget solely to make a regression pass. First capture the before/after profile and identify whether the workload or supported hardware requirement changed.
