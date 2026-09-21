# Deep diagnostic recipes

This document is the implementation contract for the diagnostic subsystem. It is intended to let another worker validate or extend the code without reconstructing the design from conversation history.

## Goals

- Start from a reproducible packaged xemu build and optional named VM snapshot.
- Make tool setup/readiness visible before spending time reproducing a bug.
- Pause, arm, resume, capture, finalize, and analyze with explicit completion conditions.
- Keep raw tool outputs and a compact result beside the normal run evidence.
- Avoid using heavy diagnostics as clean benchmark evidence.
- Work on Windows and Linux with platform-specific adapters behind one recipe schema.

## Job-level launch controls

| Field | Meaning |
| --- | --- |
| `LaunchMode` | `direct` or `renderdoc`. RenderDoc launch instruments xemu before graphics initialization. |
| `SnapshotName` | Passed as xemu/QEMU `-loadvm <name>`. The configured HDD must contain the named snapshot. |
| `StartPaused` | Adds `-S`; when QMP is enabled, the runner verifies the resulting run state is paused. |
| `RequireInput` | Requires a usable Windows SendInput or Linux X11/XTest adapter before the plan starts. |
| `Diagnostics` | Named recipe array addressable from plan steps and HTTP. |

The runner owns `-qmp`, `-loadvm`, and `-S` when their typed job fields are used. Duplicate manual arguments are rejected during preflight.

## Recipe types

### wpr

Windows only. Starts one or more built-in/user WPR profiles separated by `+`, then resumes xemu for `DurationMs`, pauses, stops the trace and optionally runs xperf `tracestats`, `profile -detail`, and `process`.

Important fields: `Profile`, `DurationMs`, `OutputName`.

### perf

Linux only. Attaches `perf record` to the actual xemu PID with configurable `Frequency` and `CallGraph`; resumes for `DurationMs`, pauses, stops perf with SIGINT, then generates a text report.

Host perf_event permissions and unwind support are prerequisites.

### renderdoc

Requires `LaunchMode: renderdoc`.

Preferred mode:

```json
{
  "Id": "guest-frame",
  "Type": "renderdoc",
  "RenderDocTrigger": "xemu-hotkey",
  "Frames": 1,
  "TracePgraph": true,
  "AnalyzeCapture": true
}
```

The Python bridge establishes target control first. Once armed, the runner sends F10 to xemu. Shift selects five frames; Ctrl asks xemu to enable its existing PGRAPH trace around capture. Only 1 or 5 frames are accepted for this mode because those are the semantics implemented by xemu's shortcut.

`target-control` is also supported for one generic RenderDoc trigger, but `TracePgraph` is rejected there because it bypasses xemu's bounded trace hook.

### hang_bundle

Best-effort QMP status and PNG, plus:
- Windows: ProcDump `-ma`.
- Linux: GDB all-thread full backtrace plus generated core.

Use automatic hang bundles for watchdog failures. Full dumps may be large.

### memory_dump

Calls QMP `pmemsave` with an exact guest physical address and size. The file size must match; a SHA-256 sidecar is retained. This is appropriate for known suspect VRAM/RAM ranges, not automatic writer attribution.

### symbolize

Runs configured addr2line with `-a -f -i -C` against a package-local debug file and explicit addresses. Exact build/debug identity remains the caller's responsibility.

### qmp

Executes a package-declared QMP command and retains the returned JSON. Use this for stable QMP queries or trace controls that do not justify a dedicated adapter.

### monitor

Executes a package-declared HMP command through `human-monitor-command` and retains text. Typical diagnostics include `info registers`, `info mtree`, and other monitor queries supported by that xemu build.

### external

Runs another installed executable with literal argument items. Supported placeholders:

- `{pid}`
- `{diagnosticDir}`
- `{resultDir}`
- `{packageDir}`
- `{runId}`

The working directory may be `diagnostic`, `result`, or a package-relative directory. stdout/stderr/command evidence are retained. This is the intended adapter for VTune/Nsight/custom analysis tools before they warrant native C# support.

## HTTP

```text
GET  /diagnostics
GET  /api/v1/diagnostics
GET  /api/v1/diagnostics/tools
GET  /api/v1/diagnostics/recipes
POST /api/v1/diagnostics/run
```

POST a configured recipe:

```json
{"Id":"cpu-window"}
```

or an ad-hoc recipe:

```json
{"Recipe":{"Id":"regs","Type":"monitor","MonitorCommand":"info registers"}}
```

Calls are synchronous per HTTP request and diagnostics are serialized server-side. A second request waits for the diagnostic gate rather than overlapping a heavyweight trace.

## Evidence contract

Each diagnostic directory contains `request.json` and `result.json`. Tool adapters add raw artifacts and command/stdout/stderr evidence. A failed recipe still gets a result document.

The run's `operator-events.jsonl` records diagnostic invocation. Final result metadata includes completed diagnostic results and therefore does not silently classify an instrumented run as a clean benchmark.

## Known validation boundary

The authoring environment does not have the .NET SDK, WPR, perf privileges, xemu GUI, or RenderDoc Python installation needed for end-to-end qualification. Treat the code as implementation-complete for handoff, not native-qualified.

See `VALIDATION.md` for the exact checks the next worker must run.
