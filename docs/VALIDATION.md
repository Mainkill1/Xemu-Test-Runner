# Validation record

Date: 2026-09-22. Base commit: `4dd9cf93c243dd16dadf4c59bc81d28e3e58cd5c`.

This record separates deterministic runner checks from native xemu qualification. A successful runner job proves orchestration and evidence retention; it does not prove that the emulated game rendered correctly or met a performance target.

## Build and regression results

The repository is pinned to .NET SDK 10.0.401. Release builds use deterministic compilation and treat warnings as errors.

| Check | Environment | Result |
| --- | --- | --- |
| Release build | Debian Linux, .NET 10.0.401 | Pass, 0 warnings / 0 errors |
| RunnerChecks | Debian Linux x64 | 32/32 pass |
| RunnerChecks | Windows x64 native test host | 32/32 pass |
| RunnerChecks | Steam Deck / SteamOS x64 | 32/32 pass |
| Self-contained single-file publish | `linux-x64` | Pass; executable starts and reports version |
| Self-contained single-file publish | `win-x64` | Pass; executable starts and reports version |
| Browser fixtures | Chromium via Playwright 1.55.0 | Pass; no browser-script errors |
| Python diagnostic helpers | Python bytecode compilation | Pass |
| GitHub Actions workflow | Linux, Windows, browser matrix | Pass |

RunnerChecks includes one process-level integration. It copies its own executable into a queued package, launches it as a controlled QMP target, exercises the live HTTP endpoint, telemetry, stop/continue, an unsupported-QMP screenshot with external fallback, a QMP quit that disconnects without replying, result publication, and exactly-once package archival. A separate check pins the product default to HTTP enabled on `0.0.0.0:9368`; the process fixture itself uses loopback to avoid exposing a CI listener.

The suite also covers experiment routing, benchmark-operation rejection with request bodies, terminal-loss resilience, and transient/persistent finalized-package archive locks. The controlled QMP fixture remains a host process rather than a renderer test.

## Native Steam Deck result

The Linux candidate was exercised against a real xemu AppImage built from source `9f756c9ba03640020017ce5bbd275f3ff06a465e`, reporting xemu 0.8.136.

### Bounded control and telemetry smoke

- xemu reached QMP readiness.
- The final `4dd9cf9` replay retained 115 telemetry samples at a 100 ms target interval.
- Monitoring reported zero overruns and zero dropped write samples.
- The Linux DRM sysfs GPU provider was active.
- Current xemu reported `CommandNotFound` for QMP `screendump`; the configured `ffmpeg` X11 fallback produced a validated 1280×800 PNG.
- The final `quit` step completed even though xemu reset QMP without returning a reply.
- xemu exited with code 0; the job status was `completed` and its package moved to Tested once.
- No xemu, runner, or capture process remained afterward.

This short smoke reached xemu startup and renderer initialization. It did not qualify game correctness or frame-time performance.

## Native Windows result

The final runner was launched in the interactive desktop session and exercised against a real Vulkan xemu build and an existing snapshot-backed configuration.

- xemu reached QMP readiness, loaded the snapshot, and completed the declared ten-second plan.
- The final QMP `quit` produced exit code 0; evidence was complete and comparison-eligible under the smoke contract.
- The final replay retained 98 telemetry samples with zero dropped writes. NVIDIA NVML and Windows GPU performance counters were both active.
- No input, pause, preview, bulk-transfer, diagnostic, or host-state intervention was recorded.
- The package moved from Testing to Tested exactly once, the runner remained healthy, and no xemu process remained.

The native run exposed a Windows archive race before the final fix: xemu exited and durable evidence was complete, but a newly written Vulkan cache file remained transiently locked during the package move. The final runner retries archive moves for the process-exit timeout and leaves a persistent lock in a durable `package_archive_busy` / `queue_blocked` state instead of terminating. Startup recovery also archived the already-finalized reproducer.

At a 100 ms target interval, the final Windows replay reported 11 collector overruns, 34.67% average collector duty, and a 220.83 ms maximum collection. Those values are truthful evidence that this full Windows GPU provider configuration is too intrusive for acceptance timing at that cadence. Performance campaigns must calibrate a slower interval or reduced provider set and quantify overhead before comparing emulator builds.

### Native `perf` diagnostic

A second real-xemu job paused the VM, attached `/usr/bin/perf`, resumed for a three-second diagnostic window at 499 Hz with DWARF call graphs, paused, produced a report, and quit.

| Evidence | Result |
| --- | ---: |
| Job status / exit | `completed` / 0 |
| Diagnostic status | `completed` |
| `perf.data` size | 37,746,376 bytes |
| Samples in generated report | about 4,000 |
| Lost samples | 0 |
| Runner telemetry samples | 62 |
| Telemetry overruns / dropped writes | 0 / 0 |

The result correctly records one diagnostic intervention and `comparisonStatus: operator_intervened`. This verifies PID attribution, capture finalization, report generation, and teardown. It is not an uninstrumented performance result.

### Remote HTTP control plane

A third real-xemu run enabled the normal HTTP endpoint at `0.0.0.0:9368`. A different LAN machine successfully queried `/api/v1/status` while the run was active and received the run ID, xemu PID, queue state, live host/process metrics, DRM GPU utilization/VRAM data, and `HttpEndpoint: http://0.0.0.0:9368`.

The job completed with exit code 0 after 153 telemetry samples, with zero collector overruns and zero dropped writes. No xemu, runner, or `perf record` process remained. This proves real remote reachability on the tested network; firewall and network policy remain host responsibilities.

## Browser and API scope

The browser fixture reconstructs the home, control, and evidence pages with controlled `fetch` responses. It verifies statistics/intervention rendering, controller requests, recorder updates, artifact links, and bounded tail display. The process integration separately reaches the real embedded HTTP listener and waits for a non-null active run ID.

Neither check is a 10 GiB transfer qualification. The optional `--large-file` RunnerChecks mode validates 64-bit local length/range handling with an 11 GiB logical file; it does not measure network throughput or interruption recovery.

Live cross-platform API checks used the exact final binaries:

- benchmark policy rejected input, pause, and file-upload requests with structured `409 operation_blocked` responses on Windows and Steam Deck; rejected operations did not count as intervention and created no files;
- the experiment endpoint returned the same eligible-attempt aggregation as the CLI;
- SHA-256-protected uploads returned `201` and downloaded byte-identically; an incorrect digest returned `422 upload_hash_mismatch` and left no published target;
- immutable input manifests and retained runtime-state copies matched their declared hashes.

## Remaining qualification

The following paths are implemented but were not qualified in this pass:

1. Real xemu screenshot fallback on Windows. Real process/QMP/telemetry/quit/archive automation passed, but screenshot capture was not part of the final Windows smoke.
2. Native controller input on Windows and X11/XWayland, including cancellation while a key is held and focus rejection.
3. WPR/xperf, ProcDump, GDB/core, memory-dump, symbolization, and external-tool recipes against their real tools.
4. RenderDoc launch injection, xemu F10 capture, replay inventory, and snapshot restoration against an actual game frame.
5. A real 10 GiB+ HTTP upload/download/resume and simultaneous-writer rejection.
6. Deliberate crash, QMP hang, low-disk, unwritable-result, and missing-runtime-library recovery on native hosts. Finalized-package restart recovery passed on Windows.
7. Measured overhead comparisons with monitoring, preview, and diagnostics independently enabled and disabled.

Do not convert unavailable counters to zero, call a QMP response guest progress, or compare diagnostic/intervened runs with ordinary performance runs.
