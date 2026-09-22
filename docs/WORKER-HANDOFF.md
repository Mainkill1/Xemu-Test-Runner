# Worker handoff

## Branch and verification

Work on `feature/trustworthy-experiment-runner` (PR #5, stacked on PR #3). Do not restart from the older `feature/reliability-and-evidence` branch.

Readiness code commit: `9f0a8e5ddde0b0bf724b9d42b6c795856c2480d7`.

GitHub Actions run `35685654019` passed:

| Gate | Windows | Linux |
| --- | --- | --- |
| Warning-free build | Passed | Passed |
| Existing regression/process-integration checks | Passed | Passed |
| Self-contained publish | Passed | Passed |

Browser fixtures and diagnostic-helper checks also passed in that run. This verifies the build and existing fixtures, not native xemu rendering, real GPU performance overhead, or every diagnostic-tool integration.

The previous known-failing recovery assertion has been corrected. There is no intentionally failing recovery test to waive: a starting/held/live-owned attempt must remain held even when a result exists; a finalized attempt with released ownership may archive without replay.

## Changes in the readiness pass

The initial branch failed Linux integration because reading `/proc/<pid>/io` could throw during target teardown and terminate the entire telemetry loop. Expected process/counter access failures are now isolated to the affected metric, reported in `Errors`, and re-primed after recovery. Host sampling continues. Process-identity access is inside the same error boundary.

System-counter code is separated by responsibility:

| File | Responsibility |
| --- | --- |
| `Monitoring/Providers/SystemMetricProvider.cs` | Sampling flow, process transitions, counter deltas, and error isolation |
| `Monitoring/Providers/SystemMetricProvider.Windows.cs` | Windows native structures and reads |
| `Monitoring/Providers/SystemMetricProvider.Linux.cs` | Linux procfs reads and parsing |

Linux memory sampling no longer builds a dictionary for every meminfo field. CPU accounting excludes the guest fields already included in user/nice, and backward/reset counters produce an unknown interval instead of an underflow spike. The legacy Windows `PageFileUsagePercent` field still represents commit pressure; do not describe it as physical pagefile occupancy.

Artifact handling now has one shared implementation:

| File | Responsibility |
| --- | --- |
| `Runtime/ArtifactInspector.cs` | Scope resolution, existence/size/hash/text requirements, bounded JSON/text reads |
| `Runtime/ArtifactConditionWaiter.cs` | Polling and readiness deadline only |
| `Runtime/WorkloadEvaluator.cs` | Aggregates independent requirements and extracts finite JSON numbers |

Artifact checks use one file handle rather than reopening the path between hash and text assertions. Text/JSON allocation is bounded to 16 MiB. Unreadable files are failures, not missing optional artifacts. A nonnumeric JSON property produces a failed measurement without throwing away other checks. Readiness deadlines cover file I/O as well as polling delays, while external cancellation remains cancellation.

The newer upload store, generation-safe recording sink, GPU discovery backoff, and actionable API error contract were retained rather than replaced with earlier versions.

## Local gates

```sh
dotnet build tests/RunnerChecks/RunnerChecks.csproj -c Release --nologo
dotnet run --project tests/RunnerChecks -c Release --no-build
python -m py_compile tools/renderdoc_session.py tools/renderdoc_analyze.py scripts/check-browser.py
```

Run the browser fixture script with its documented Playwright/Chromium prerequisites. Do not weaken ownership or evidence rules to make a fixture pass.

## Native qualification

First exercise direct xemu launch and private runtime state on both real test hosts. Confirm that the HDD/EEPROM seed stays unchanged, the launched configuration uses the private copy, and retained manifests identify the actual inputs.

Then exercise readiness markers, segment boundaries, final guest results, and repeated baseline/candidate measurements. Check that missing, malformed, and incomplete artifacts remain visible as failed requirements rather than successful execution being promoted to correctness.

During a benchmark, verify that manual previews, input, pause, diagnostics, and transfer paths are blocked as configured. During an interactive run, force a runner/control failure and validate preserved-target inspection, explicit quit, and restart recovery without duplicate xemu processes.

Qualify diagnostic adapters separately before chaining them: RenderDoc launch/PID/guest-frame capture/replay, WPR/xperf, perf, ProcDump/GDB, pmemsave, symbolization, and external recipes. Cancel each while active and inspect child-process ownership and partial evidence.

Finally measure monitoring overhead on the actual GPU/driver and validate large uploads/downloads, interrupted resume, concurrent destinations, and digest failures. Test real Windows session/display/suspend transitions separately from resource telemetry.

## Remaining boundaries

`RunnerEngine` still has a large execution method; any further decomposition should preserve prepare/launch/supervise/finalize/hold ordering and make resource ownership explicit. Avoid introducing a second sampler, recorder, or target owner merely to shorten that file.

Guest-native signaling and acknowledged emulator-side controller input are not implemented. File-based readiness requires guest tooling or extraction to publish host-visible results. `ControlledFactors` are not yet an automatic cross-run equality gate, and comparison output is descriptive aggregation, not paired scheduling or statistical significance. Physical Xbox adapters remain outside the current host implementation.

Keep these limits explicit in release notes. A passing build, zero exit code, responsive QMP connection, or readable graphics capture does not establish original-Xbox correctness.
