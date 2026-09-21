# Xemu Test Runner

A foreground C#/.NET 10 and Spectre.Console.Cli application for Windows/Linux xemu build testing. The console owns the queue, launched process, telemetry, test plan and embedded LAN HTTP endpoint. No ASP.NET service, separate daemon or physical Xbox is required.

**Validation status:** the reliability changes have source review and offline browser-fixture checks, but have not yet been compiled or exercised with real xemu on Windows/Linux. See [validation](docs/VALIDATION.md) before relying on unattended runs.

## Build and start

```sh
dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release
dotnet run --project src/XemuTestRunner -- init
dotnet run --project src/XemuTestRunner -- doctor
dotnet run --project src/XemuTestRunner -- run
```

Publish a self-contained executable with `scripts/publish.ps1 -Rid win-x64` or `bash scripts/publish.sh linux-x64`. Use the produced `XemuTestRunner.exe` or `XemuTestRunner` executable. The `run --once` command drains the queue and exits when it is empty. `queue` inspects the local queue; `status --url http://host:9368` reads a running instance.

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
    { "Type": "screenshot", "Name": "after-start" }
  ]
}
```

For Linux, set `TargetOs` to `linux` and `Executable` to `xemu`. Optionally add `ExpectedExecutableSha256` with the expected 64-character hex hash. An absent expected hash still produces an actual executable hash in evidence. A plan finishing does not terminate xemu; target exit, timeout or an explicit operator stop ends the attempt. Exit code zero means **completed**, not proven Xbox correctness.

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

This is a warning mechanism, not an automatic performance verdict. A run without interventions is `not_evaluated`, never automatically benchmark-valid. Close/disable previews during clean measurements; the 100 ms telemetry sampler stays independent of HTTP polling.

## Configuration

`init` generates `runner.json`; `runner.example.json` includes all sections. Relevant defaults:

```json
{
  "Monitoring": { "IntervalMs": 100 },
  "Ui": { "CliRefreshMs": 250, "WebRefreshMs": 500, "LivePreviewEnabled": true, "LivePreviewIntervalMs": 750 },
  "Reliability": {
    "Preflight": { "MinimumFreeSpaceBytes": 1073741824 },
    "MaxInterruptedRetries": 2,
    "ProcessExitTimeoutMs": 10000,
    "EvidenceListLimit": 100,
    "MaxPreviewBytes": 16777216,
    "Watchdog": { "Enabled": true, "StartupGraceMs": 10000, "IntervalMs": 2000, "RequestTimeoutMs": 5000, "FailureThreshold": 3 }
  }
}
```

## Dashboards and xemu control

`/` shows cached host/process statistics, job/queue status, last outcome, sample age and intervention counts. `/control` provides near-live preview, retained screenshots, pause/resume, logical Xbox buttons and a recorder that exports `Plan` JSON. Stopping a nonempty recording saves it with the active run.

The runner appends its own `-qmp tcp:127.0.0.1:<port>,server=on,wait=off`. QMP is used for status, stop/cont and PNG screendump. Availability of PNG capture depends on the xemu build and renderer; this branch still needs native verification.

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
GET  /api/v1/preview
GET  /api/v1/screenshot
POST /api/v1/xemu/pause
POST /api/v1/xemu/resume
POST /api/v1/input/press
GET  /api/v1/input/record
POST /api/v1/input/record/start
POST /api/v1/input/record/stop
POST /api/v1/input/record/clear
POST /api/v1/runner/stop
```

Telemetry includes host/process CPU, RAM, swap/pagefile, I/O and available GPU/VRAM/thermal counters. Missing values are shown as unavailable, not manufactured as GPU zero. Vendor support and update frequencies vary. Host/process samples are collected during active jobs; the idle dashboard is not an always-on system monitor.

## Large file transfers

The existing `GET/HEAD/PUT/POST /api/v1/files/<path>` interface streams artifacts under the configured file root with 64-bit lengths. Uploads require Content-Length; sequential resume uses Content-Range and `.partial` files. Query `?upload-status=1` for upload position. There is no application-wide 10 GB length cap, but disk space and filesystem limits still apply. A real 10 GB+ network transfer has not been validated in this environment.

## Tests

```sh
dotnet run --project tests/RunnerChecks -c Release
```

This dependency-free regression executable covers preflight, ownership, recovery decisions, watchdog sequences/cancellation, concurrent preview caching, bounded tails, large-range arithmetic, input ABI size and intervention accounting. `-- --large-file` opts into an 11 GiB local file-length test; it is not a 10 GB HTTP transfer test.

Optional offline browser fixtures: `python scripts/check-browser.py --browser /path/to/chromium`. Requires Python Playwright; it uses no running C# server and must not be mistaken for end-to-end xemu qualification. See [validation](docs/VALIDATION.md) and [architecture](docs/ARCHITECTURE.md).
