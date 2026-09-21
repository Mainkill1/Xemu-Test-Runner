# Validation record

Date: 2026-09-21. Base commit: `8882e08064f0c8cdabe6350dc4d9d7788bf6c736`.

This record separates deterministic runner checks from native xemu qualification. A successful runner job proves orchestration and evidence retention; it does not prove that the emulated game rendered correctly or met a performance target.

## Build and regression results

The repository is pinned to .NET SDK 10.0.401. Release builds use deterministic compilation and treat warnings as errors.

| Check | Environment | Result |
| --- | --- | --- |
| Release build | Debian Linux, .NET 10.0.401 | Pass, 0 warnings / 0 errors |
| RunnerChecks | Debian Linux x64 | 20/20 pass |
| RunnerChecks | Windows x64 native test host | 20/20 pass |
| RunnerChecks | Steam Deck / SteamOS x64 | 20/20 pass |
| Self-contained single-file publish | `linux-x64` | Pass; executable starts and reports version |
| Self-contained single-file publish | `win-x64` | Pass; executable starts and reports version |
| Browser fixtures | Chromium via Playwright 1.55.0 | Pass; no browser-script errors |
| Python diagnostic helpers | Python bytecode compilation | Pass |
| GitHub Actions workflow | `actionlint` 1.7.12 | Pass |

RunnerChecks includes one process-level integration. It copies its own executable into a queued package, launches it as a controlled QMP target, exercises the live HTTP endpoint, telemetry, stop/continue, an unsupported-QMP screenshot with external fallback, a QMP quit that disconnects without replying, result publication, and exactly-once package archival. A separate check pins the product default to HTTP enabled on `0.0.0.0:9368`; the process fixture itself uses loopback to avoid exposing a CI listener.

That fixture reproduces current xemu's shutdown behavior, but it remains a controlled host rather than a renderer test.

## Native Steam Deck result

The Linux candidate was exercised against a real xemu AppImage built from source `9f756c9ba03640020017ce5bbd275f3ff06a465e`, reporting xemu 0.8.136.

### Bounded control and telemetry smoke

- xemu reached QMP readiness.
- The final candidate retained 105 telemetry samples at a 100 ms target interval.
- Monitoring reported zero overruns and zero dropped write samples.
- The Linux DRM sysfs GPU provider was active.
- Current xemu reported `CommandNotFound` for QMP `screendump`; the configured `ffmpeg` X11 fallback produced a validated 1280×800 PNG.
- The final `quit` step completed even though xemu reset QMP without returning a reply.
- xemu exited with code 0; the job status was `completed` and its package moved to Tested once.
- No xemu, runner, or capture process remained afterward.

This short smoke reached xemu startup and renderer initialization. It did not qualify game correctness or frame-time performance.

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

## Remaining qualification

The following paths are implemented but were not qualified in this pass:

1. Real xemu automation and screenshot fallback on Windows. The Windows process/QMP integration passed with the controlled fixture.
2. Native controller input on Windows and X11/XWayland, including cancellation while a key is held and focus rejection.
3. WPR/xperf, ProcDump, GDB/core, memory-dump, symbolization, and external-tool recipes against their real tools.
4. RenderDoc launch injection, xemu F10 capture, replay inventory, and snapshot restoration against an actual game frame.
5. A real 10 GiB+ HTTP upload/download/resume and simultaneous-writer rejection.
6. Deliberate crash, QMP hang, low-disk, unwritable-result, missing-runtime-library, and interrupted-run recovery on native hosts.
7. Measured overhead comparisons with monitoring, preview, and diagnostics independently enabled and disabled.

Do not convert unavailable counters to zero, call a QMP response guest progress, or compare diagnostic/intervened runs with ordinary performance runs.
