# Xemu Test Runner

A foreground C#/.NET 10 and Spectre.Console.Cli application for Windows/Linux xemu build testing. The console owns the queue, launched process, telemetry, test plan and embedded LAN HTTP endpoint. No ASP.NET service, separate daemon or physical Xbox is required.

**Validation status:** a warning-free release build, both self-contained target publishes, and browser fixtures pass on Linux; the regression/process suite passes on Linux, native Windows, and Steam Deck. A real xemu QMP/telemetry/remote-HTTP/`perf` run also passes on Steam Deck. See [validation](docs/VALIDATION.md) for exact coverage and remaining limits.

## Build and start

```sh
dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release
dotnet run --project src/XemuTestRunner -- init
dotnet run --project src/XemuTestRunner -- doctor
dotnet run --project src/XemuTestRunner -- run
```

Publish a self-contained executable with `scripts/publish.ps1 -Rid win-x64` or `bash scripts/publish.sh linux-x64`. Use the produced `XemuTestRunner.exe` or `XemuTestRunner` executable. The `run --once` command drains the queue and exits when it is empty. `queue` inspects the local queue; `status --url http://host:9368` reads a running instance.

## Automation / AI-friendly operation

The runner has a non-interactive mode intended for agents, scripts, CI, and remote orchestration. It never requires terminal input.

Run at most one queued package:

```sh
xemu-test-runner run --one-shot --non-interactive
```

Run one package and return exactly one JSON document on stdout:

```sh
xemu-test-runner run --one-shot --json
```

Drain the queue and return one final JSON summary:

```sh
xemu-test-runner run --once --json
```

`--json` implies non-interactive mode and suppresses the Spectre live dashboard. Stdout is reserved for the final JSON object so an agent does not need to scrape terminal formatting.

Example result:

```json
{
  "ok": true,
  "exitCode": 0,
  "cancelled": false,
  "mode": "one-shot",
  "phase": "finished",
  "jobsFinished": 1,
  "failedJobs": 0,
  "lastJob": "build-123",
  "lastResult": "completed",
  "evidenceDirectory": "workspace/Results/...",
  "resultFile": "workspace/Results/.../result.json",
  "queue": {
    "pending": 2,
    "testing": 0,
    "tested": 41
  },
  "httpEndpoint": "http://192.168.1.42:9368"
}
```

Automation exit codes are stable. A successful process launch is not enough for comparison eligibility; use `assessment.json` / `result.json.assessment` for correctness, evidence, and comparison state.

Automation exit codes are stable:

| Exit | Meaning |
| ---: | --- |
| 0 | Requested work completed with no failed jobs and no blocked queue state |
| 1 | Runner/internal failure |
| 2 | One or more tests finished with a non-`completed` result |
| 3 | Queue/package state blocked execution |
| 130 | Cancelled/interrupted |

`--non-interactive` without `--json` emits plain state changes instead of a repainting terminal UI. This is useful when a log stream is desired. Redirected stdout also automatically selects non-interactive rendering.

The existing queue and status commands also expose JSON:

```sh
xemu-test-runner queue --json
xemu-test-runner status --url http://runner:9368 --json
```

A typical agent loop can therefore:

1. stage a complete package under `.incoming-...` and atomically rename it into Pending;
2. run `run --one-shot --json`;
3. check the process exit code and `lastResult`;
4. open the returned `resultFile` / evidence directory;
5. make a code change and repeat.

`--one-shot` differs from `--once`: one-shot processes at most one claimed package, while once drains the current queue before exiting.

## Experiment validity model

The runner separates four questions that used to be collapsed into one `status`:

| Outcome | Meaning |
| --- | --- |
| Execution | Did the runner launch/control/finish the attempt correctly? |
| Correctness | Did declared guest/workload assertions pass? |
| Evidence | Are required artifacts and metric counts complete? |
| Comparison | Is this attempt eligible for the declared experiment? |

Every completed attempt writes `assessment.json` and includes the same assessment in `result.json`. Existing jobs that do not declare correctness/evidence contracts remain valid, but those dimensions stay `notEvaluated` instead of being implied by exit code.

A benchmark package can declare:

```json
{
  "Experiment": {
    "Id": "nv2a-upload-a-b",
    "Variant": "candidate",
    "Reference": "baseline",
    "VariedFactors": ["xemu-build"],
    "ControlledFactors": ["game", "snapshot-seed", "xemu-config", "input-plan"],
    "RequireCorrectnessPass": true,
    "RequireCompleteEvidence": true,
    "AllowOperatorIntervention": false,
    "AllowDiagnostics": false
  },
  "Operations": {
    "Mode": "benchmark"
  }
}
```

Benchmark operation policy blocks manual preview/screenshot/input/pause and bulk-transfer operations by default while the test is active. Those operations can be explicitly allowed, but the comparison contract still decides whether the resulting attempt is eligible.

Aggregate eligible measurements later with:

```sh
xemu-test-runner compare --experiment nv2a-upload-a-b
xemu-test-runner compare --experiment nv2a-upload-a-b --json
```

Ineligible attempts remain visible with exclusion reasons; they are not silently averaged into the result.

## Private runtime state and input identity

Mutable test state should not be shared between attempts. `RuntimeState` copies declared package seed files into:

```text
workspace/Runtime/<run-id>/
```

before launch. Job arguments/environment values may use:

```text
{runtimeDir}
{packageDir}
{resultDir}
{runId}
```

Example:

```json
{
  "RuntimeState": {
    "Enabled": true,
    "KeepOnFailure": true,
    "KeepOnSuccess": false,
    "Files": [
      {
        "Source": "state/seed-hdd.qcow2",
        "Destination": "hdd.qcow2"
      }
    ]
  },
  "Inputs": [
    {
      "Path": "xemu.toml",
      "Role": "emulator-config",
      "Hash": true
    },
    {
      "Path": "test.iso",
      "Role": "workload",
      "Hash": true
    }
  ]
}
```

The runner writes `input-manifest.json` with the frozen job hash, executable hash, declared input identity, and runtime seed hashes. Runtime files are retained on failure by default and deleted after successful valid attempts unless configured otherwise.

## Workload assertions and measurement segments

A job may declare correctness and evidence requirements:

```json
{
  "Workload": {
    "MinimumMetricSamples": 100,
    "CorrectnessChecks": [
      {
        "Name": "guest-result",
        "Scope": "result",
        "Path": "guest-results.json",
        "ContainsText": "\"passed\":true"
      }
    ],
    "EvidenceRequirements": [
      {
        "Name": "final-frame",
        "Scope": "result",
        "Path": "screenshots/final.png",
        "MinimumBytes": 1000
      }
    ],
    "ReportedMetrics": [
      {
        "Name": "average-frame-ms",
        "Scope": "result",
        "Path": "guest-results.json",
        "JsonProperty": "timing.averageFrameMs",
        "Unit": "ms",
        "Direction": "lower"
      }
    ]
  }
}
```

Plan steps can mark the exact measurement interval:

```json
{"Type":"segment_start","Name":"steady-state"}
{"Type":"wait","DelayMs":30000}
{"Type":"segment_end","Name":"steady-state"}
```

The segment name is written to `metrics.csv` and start/end boundaries are retained in `segments.jsonl`.

For readiness/progress that should not rely on fixed sleeps, use:

```json
{
  "Type": "wait_for_artifact",
  "TimeoutMs": 30000,
  "PollIntervalMs": 100,
  "Condition": {
    "Scope": "result",
    "Path": "guest-ready.json",
    "ContainsText": "\"ready\":true"
  }
}
```

This waits for a declared host-visible artifact condition with a hard deadline.


## A queue item contains the plan AND candidate executable

```text
workspace/
  Queue/
    Pending/
      build-123/
        job.json
        xemu.exe             # or xemu on Linux
        xemu.toml
        required DLLs/assets...
    Testing/
    Tested/
  Results/
  Files/
```

The **whole build directory** moves `Pending -> Testing -> Tested`. Only one visible package may occupy Testing. Keep these directories on the same local filesystem for directory renames. Copy incomplete packages under `.incoming-build-123`, then rename to `build-123` after all files arrive. Dot-prefixed directories are ignored.

Visible packages are also stability-checked before claim. The runner fingerprints `job.json`, the candidate executable, and declared `RequiredFiles`; the critical set must remain unchanged for `Queue.PackageStabilityMs` (750 ms by default). Busy, incomplete, moved, access-denied, invalid, and changed-after-claim packages are reported as structured queue issues instead of generic runner failures. A package that changes after entering Testing is held there and is not launched.

Linux builds must retain their executable bit. Package-relative paths are used for `Executable` and `WorkingDirectory`; xemu arguments are passed separately without shell expansion. Supply your own firmware, disks and test assets; none are distributed here.

```json
{
  "Id": "build-123",
  "TargetOs": "windows",
  "Executable": "xemu.exe",
  "WorkingDirectory": ".",
  "RequiredFiles": ["xemu.toml"],
  "Arguments": ["-config_path", "xemu.toml"],
  "TimeoutSeconds": 120,
  "Plan": [
    { "Type": "wait", "DelayMs": 3000 },
    { "Type": "button", "Button": "Start", "DurationMs": 100 },
    { "Type": "screenshot", "Name": "after-start" },
    { "Type": "quit" }
  ]
}
```

For Linux, set `TargetOs` to `linux` and `Executable` to `xemu`. Optionally add `ExpectedExecutableSha256` with the expected 64-character hex hash. An absent expected hash still produces an actual executable hash in evidence. A plan finishing does not terminate xemu unless its final step is `quit`; target exit, timeout or an explicit operator stop also ends the attempt. Exit code zero means **completed**, not proven Xbox correctness.

## Five reliability additions

### 1. Preflight before launch

`validate <package>` runs the same checks used by the queue without launching xemu:

```sh
dotnet run --project src/XemuTestRunner -- validate workspace/Queue/Pending/build-123
```

Checks cover the declared target OS, executable presence/hash, required files, working directory, Linux executable permission, and minimum free space on the result volume. A queued failure writes `preflight.json` and `result.json` with `preflight_failed` instead of launching an incomplete build. Malformed manifests receive `invalid_job`. Target OS is declared metadata, not PE/ELF architecture inspection.

### 2. Exclusive ownership and bounded recovery

Workspace and Testing-directory leases prevent a second runner using the same locations. Each package has an atomically replaced `.runner-attempt.json` recording attempt number, run ID, launch phase and process identity. Final results are written before moving to Tested.

On restart, an already-finalized attempt is archived without rerunning it. An absent/ambiguous identity, a live matching process or an uninspectable process leaves the package in Testing for inspection. A known exited interruption can be retried up to `MaxInterruptedRetries`; previous attempts retain their own result records. `Queue.InterruptedAction: "hold"` suppresses automatic retries. No recovery operation kills an arbitrary PID.

Do not delete a live `.runner.lock`. For a held package, inspect `.runner-recovery.json`, its attempt journal and the recorded process before manually requeuing it. Filesystem flushing and renames reduce loss windows; they are not a universal power-loss guarantee.

### 3. QMP responsiveness watchdog

An optional watchdog probes xemu's local QMP endpoint after a startup grace period. Only consecutive failed probes trigger `unresponsive`; successful probes reset the count. The normal test timeout remains separate. A paused VM can still answer QMP.

A QMP response is **not** proof that the game is advancing, nor does an unchanged screenshot prove a hang. The result retains the watchdog reason, last successful probe time and failure count. Timeout/plan-failure handling attempts a bounded failure screenshot before stopping the process; a dead or wedged renderer may not provide one. Process shutdown and log draining are bounded. If cleanup cannot be confirmed, Testing is retained and the queue stops.

### 4. Evidence browser, log tails and downloads

Open `http://host:9368/results`. It lists recent runs, result metadata, artifacts and a followable bounded log tail. Selecting a log reads only its final slice, not a multi-gigabyte file into memory.

```text
GET /api/v1/runs
GET /api/v1/runs/<run-id>
GET /api/v1/runs/<run-id>/tail?file=stdout.log&bytes=32768
GET /api/v1/runs/<run-id>/artifacts/result.json
GET /api/v1/runs/<run-id>/artifacts/metrics.csv
```

Tails support `stdout.log`, `stderr.log` and `operator-events.jsonl`, with a maximum 64 KiB slice and explicit offsets. Artifact GET/HEAD supports one HTTP byte range using 64-bit lengths. Listing sizes are bounded; linked or escaping evidence paths are rejected. This is not a database-backed history index.

### 5. Benchmark hygiene and shared preview

Preview requests share one latest frame per run and a server-side minimum capture interval. Concurrent viewers do not each start a capture; failed captures are backed off too. Run IDs and capture timestamps are returned with frames. Browser refreshes are sequential, and hidden tabs stop requesting preview images.

Manual input, pauses, retained screenshots, preview captures and bulk transfers are journaled to `operator-events.jsonl`. The result includes `operatorActivity` and `comparisonStatus: "operator_intervened"` when these occurred. In-flight transfers begun before a job also mark the new job. The home page and console display these counts through `/api/v1/quality`.

This is a warning mechanism, not an automatic performance verdict. A run without interventions is `not_evaluated`, never automatically benchmark-valid. Close/disable previews during clean measurements; telemetry sampling stays independent of HTTP polling.

### Telemetry cost and recording model

There is one telemetry loop for the lifetime of the runner. While idle it samples host state into memory only so the CLI/API remain live without writing telemetry files. When xemu starts, that same collector attaches the target process and begins `metrics.csv`; when the run ends it detaches xemu and stops disk recording while host sampling continues.

Cheap host/process CPU, RAM, and process-I/O counters use `Monitoring.IntervalMs` (100 ms by default). GPU engine utilization is provider-cached at `Gpu.SampleIntervalMs` (250 ms by default); sensor-style values such as thermal/power/VRAM refresh at `Gpu.SensorIntervalMs` (1 second by default). Windows GPU counter instance discovery stays on the slower `CounterRefreshMs` cadence. This avoids calling every expensive provider at the 100 ms base rate.

Each sample records collector duration and duty-cycle. Run results retain sample count, overruns, dropped writes, average collector duty, maximum collector duty, and maximum collection duration. Those values are the first place to look when deciding whether telemetry is perturbing a benchmark.


## Deep diagnostics and snapshot recipes

Diagnostic recipes are part of the same packaged job as the xemu build. They can run from the plan or on demand from `http://host:9368/diagnostics`.

The host implementation currently supports:

| Type | Purpose |
| --- | --- |
| `wpr` | Windows WPR capture around a timed unpaused workload, followed by xperf trace/profile/process exports when xperf is available. |
| `perf` | Linux `perf record` attached to the xemu PID, followed by a text `perf report`. |
| `renderdoc` | Launch-time RenderDoc injection plus target control. Preferred `xemu-hotkey` mode triggers xemu's existing F10 guest-renderer hook; optional PGRAPH tracing uses Ctrl+F10. |
| `hang_bundle` | QMP state/screenshot plus ProcDump on Windows or GDB backtrace/core on Linux. |
| `memory_dump` | QMP `pmemsave` of an exact guest physical range with SHA-256 metadata. |
| `symbolize` | Batch `addr2line -a -f -i -C` against a package-local debug artifact. |
| `qmp` | Execute a package-declared QMP command and retain its JSON result. |
| `monitor` | Execute a package-declared HMP command through `human-monitor-command` and retain text output. |
| `external` | Run another installed host diagnostic using argument placeholders such as `{pid}`, `{diagnosticDir}`, `{resultDir}`, `{packageDir}`, and `{runId}`. |

Use:

```sh
xemu-test-runner tools
```

to show resolved tools and whether Python can import RenderDoc's `renderdoc` module. Tool probing is cached briefly so the diagnostics page does not spawn Python on every refresh.

### Snapshot → controller → RenderDoc example

The checked-in `jobs/example-diagnostic` demonstrates the intended workflow:

```text
launch candidate through RenderDoc
  -> -S
  -> -loadvm <SnapshotName>
  -> QMP ready / paused state verified
  -> host input provider verified
  -> arm RenderDoc target-control listener
  -> resume
  -> Ctrl+F10 in xemu
  -> xemu captures at its renderer frame terminator
  -> wait for NewCapture
  -> retain .rdc
  -> optional RenderDoc replay inventory
  -> pause
  -> run next diagnostic
```

`LaunchMode: "renderdoc"` is important: xemu's RenderDoc integration looks for the RenderDoc library during graphics initialization, so this mode instruments the process before normal renderer startup and holds a target-control connection open.

`RenderDocTrigger: "xemu-hotkey"` is the preferred guest-frame mode. One frame uses F10; five frames use Shift+F10; `TracePgraph: true` adds Ctrl. Generic target-control capture is also available for a single presentation frame, but it is not claimed to be equivalent to xemu's guest-renderer boundary.

Snapshots are loaded with xemu/QEMU's existing `-loadvm` support. `StartPaused: true` adds `-S` and the runner verifies QMP still reports a paused VM after restore before continuing.

`RequireInput: true` verifies the configured host input adapter is available. With the current xemu interface, the job's `xemu.toml` is still responsible for binding keyboard input to the Xbox controller port; the runner does not claim it can query physical controller topology.

### Recipe plan steps

Plans additionally support:

```json
{"Type":"pause"}
{"Type":"resume"}
{"Type":"require_input"}
{"Type":"diagnostic","DiagnosticId":"guest-frame"}
{"Type":"quit"}
```

`quit` must be the final step. Current xemu closes its QMP connection while processing a successful quit instead of returning a command reply. The runner accepts that disconnect only after the owned xemu process exits.

A diagnostic has its own pause/resume policy. WPR/perf windows count unpaused runner time, so operator pauses do not silently consume the requested measurement interval.

Automatic watchdog failures can request a hang bundle before xemu is terminated. `Diagnostics.AutoFailureBundle` can do the same for other failed runs, but defaults off because full process dumps can be large.

Every diagnostic writes under:

```text
Results/<run-id>/diagnostics/<timestamp>-<recipe-id>/
  request.json
  result.json
  raw tool artifacts...
  command/stdout/stderr evidence...
  optional analysis outputs...
```

These captures are explicitly diagnostic/intervened evidence. They are not clean benchmark measurements.


## Configuration

`init` generates `runner.json`; `runner.example.json` includes all sections. Relevant defaults:

```json
{
  "Queue": {
    "PackageStabilityMs": 750,
    "FiniteWaitTimeoutSeconds": 30
  },
  "Monitoring": {
    "IntervalMs": 100,
    "Gpu": {
      "SampleIntervalMs": 250,
      "SensorIntervalMs": 1000,
      "CounterRefreshMs": 5000
    }
  },
  "XemuControl": {
    "ScreenshotProvider": "auto",
    "ScreenshotExecutable": "",
    "ScreenshotArguments": []
  },
  "Ui": { "CliRefreshMs": 250, "WebRefreshMs": 500, "LivePreviewEnabled": true, "LivePreviewIntervalMs": 750 },
  "Reliability": {
    "Preflight": { "MinimumFreeSpaceBytes": 1073741824 },
    "MaxInterruptedRetries": 2,
    "ProcessExitTimeoutMs": 10000,
    "EvidenceListLimit": 100,
    "MaxPreviewBytes": 16777216,
    "Watchdog": { "Enabled": true, "StartupGraceMs": 10000, "IntervalMs": 2000, "RequestTimeoutMs": 5000, "FailureThreshold": 3 }
  },
  "Diagnostics": {
    "Enabled": true,
    "ToolTimeoutMs": 15000,
    "CaptureFinalizeTimeoutMs": 60000,
    "AutoHangBundle": true,
    "AutoFailureBundle": false,
    "RenderDocPythonPath": null
  }
}
```

## Dashboards and xemu control

`/` shows cached host/process statistics, job/queue status, last outcome, sample age and intervention counts. `/control` provides near-live preview, retained screenshots, pause/resume, logical Xbox buttons and a recorder that exports `Plan` JSON. Stopping a nonempty recording saves it with the active run.

HTTP is enabled on `0.0.0.0:9368` by default so a test appliance can be inspected and controlled from another LAN machine. Use `127.0.0.1` only when remote access is intentionally disabled. The server has no authentication or TLS; restrict port 9368 to the trusted test network with the host firewall.

The runner appends its own `-qmp tcp:127.0.0.1:<port>,server=on,wait=off`. QMP is used for status and stop/cont. `ScreenshotProvider: "auto"` tries QMP `screendump` first, then uses the configured external command only when xemu reports that capture command unavailable. `qmp` requires QMP capture; `external` skips the QMP attempt. External arguments are passed without a shell, must contain `{path}`, and may also use `{pid}`. A zero exit code is insufficient: the runner validates that the requested file exists and has a PNG signature.

Current xemu release builds can omit QMP `screendump`. A working X11/XWayland fallback is, with the dimensions adjusted for the host:

```json
{
  "ScreenshotProvider": "auto",
  "ScreenshotExecutable": "/usr/bin/ffmpeg",
  "ScreenshotArguments": ["-hide_banner", "-loglevel", "error", "-y", "-f", "x11grab", "-video_size", "1280x800", "-i", ":0", "-frames:v", "1", "{path}"]
}
```

The runner process needs the graphical session's `DISPLAY` and `XAUTHORITY`. Compositor screenshot tools may delegate asynchronously or require a portal; verify they actually create `{path}` before adopting them. See the [Steam Deck guide](docs/STEAM_DECK.md).

Xbox buttons are **not QMP send-key**. xemu reads SDL host keyboard state for its keyboard-as-controller mapping. Bind the packaged xemu config accordingly:

```toml
[input]
auto_bind = false
[input.bindings]
port1 = 'keyboard'
port1_driver = 'usb-xbox-gamepad'
```

Windows uses foreground-checked SendInput; Linux uses X11/XTest. Keys are released in cancellation cleanup. Native Wayland is not supported. `XemuControl.ButtonKeys` must agree with the xemu keyboard mapping. These are digital keyboard-mapped controls, not analog hardware-controller emulation.

```text
GET  /api/v1/status
GET  /api/v1/control
GET  /api/v1/metrics/latest
GET  /api/v1/queue
GET  /api/v1/quality
GET  /api/v1/diagnostics
GET  /api/v1/diagnostics/tools
GET  /api/v1/diagnostics/recipes
POST /api/v1/diagnostics/run
GET  /api/v1/preview
GET  /api/v1/screenshot
POST /api/v1/xemu/pause
POST /api/v1/xemu/resume
POST /api/v1/xemu/quit
POST /api/v1/input/press
GET  /api/v1/input/record
POST /api/v1/input/record/start
POST /api/v1/input/record/stop
POST /api/v1/input/record/clear
POST /api/v1/runner/stop
GET  /api/v1/experiments/<experiment-id>
```

Telemetry includes host/process CPU, RAM, swap/pagefile, I/O and available GPU/VRAM/thermal counters. Missing values are shown as unavailable, not manufactured as GPU zero. Vendor support and update frequencies vary. Host values are sampled continuously from runner startup; process values and CSV recording begin only while a test is active.

## Large file transfers

The existing `GET/HEAD/PUT/POST /api/v1/files/<path>` interface streams artifacts under the configured file root with 64-bit lengths. Uploads require Content-Length; sequential resume uses Content-Range and `.partial` files. Query `?upload-status=1` for upload position. Uploads to the same destination are serialized/rejected rather than interleaved. Send `X-Content-SHA256: <64-hex-digest>` to require whole-file SHA-256 verification before a completed upload is accepted. There is no application-wide 10 GB length cap, but disk space and filesystem limits still apply. A real 10 GB+ network transfer has not been validated in this environment.

## Tests

```sh
dotnet run --project tests/RunnerChecks -c Release
```

This dependency-free regression executable covers covering the remote-listener default, preflight, ownership, recovery decisions, watchdog sequences/cancellation, concurrent preview caching, bounded tails, large-range arithmetic, input ABI size, intervention accounting, diagnostic schema/reference validation, tool discovery, release identity, quit ordering, and a full queued-process/QMP/HTTP/telemetry/screenshot-fallback lifecycle. `-- --large-file` opts into an 11 GiB local file-length test; it is not a 10 GB HTTP transfer test.

Optional offline browser fixtures: `python scripts/check-browser.py --browser /path/to/chromium`. Requires Python Playwright; it uses no running C# server and must not be mistaken for end-to-end xemu qualification. See [validation](docs/VALIDATION.md), [architecture](docs/ARCHITECTURE.md), and [trustworthy experiment contracts](docs/TRUSTWORTHY-EXPERIMENTS.md).
