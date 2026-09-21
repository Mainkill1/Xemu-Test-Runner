# Xemu Test Runner

Host-side Windows/Linux runner for repeatable xemu build testing. It runs one queued xemu build at a time, records host/process performance telemetry, executes simple controller-driven test plans, captures screenshots and logs, survives runner crashes without losing queue state, and exposes a small LAN HTTP interface while the foreground CLI is running.

This repository currently targets the host only. No Original Xbox test executable or physical Xbox is required. A physical-Xbox adapter can be added later without changing the host queue/result model.

## Queue model

A queue item is a **directory containing the exact xemu build being tested and its job plan**.

    workspace/
    |-- Queue/
    |   |-- Pending/
    |   |   `-- build-123/
    |   |       |-- job.json
    |   |       |-- xemu.exe          # Windows package
    |   |       |   # or xemu         # Linux package
    |   |       |-- xemu.toml
    |   |       `-- other test files...
    |   |-- Testing/
    |   `-- Tested/
    |-- Results/
    `-- Files/

The **whole package directory** moves through:

    Pending/build-123
        -> Testing/build-123
        -> Tested/build-123

This keeps the plan and exact executable together. A job can never silently begin testing a different xemu binary because another file elsewhere was replaced.

Only one package is allowed in `Testing` at a time.

If the runner or host dies while a package is in `Testing`, the directory remains there. On the next start:

- `InterruptedAction = retry` records the interruption and moves the package back to Pending.
- `InterruptedAction = hold` leaves it in Testing for manual inspection.

A normal xemu crash, non-zero exit, timeout, or failed plan is still a completed test attempt. Its result is recorded and its package moves to Tested.

### Safely staging a package

Directories beginning with `.` are ignored by the queue. This is useful when copying a large build:

    Pending/.incoming-build-123/
        job.json
        xemu.exe
        xemu.toml

After the copy is complete, rename it:

    .incoming-build-123 -> build-123

The runner can then claim the complete package with a same-filesystem directory rename.

## Build

Requires .NET 10 SDK.

    dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release

Spectre.Console.Cli is used for the CLI. The application does not use ASP.NET, Generic Host, Windows Service, or systemd hosting.

Self-contained single-file examples:

Windows PowerShell:

    ./scripts/publish.ps1 -Rid win-x64

Linux:

    bash ./scripts/publish.sh linux-x64

## First run

Create a local config and workspace:

    xemu-test-runner init

Or copy `runner.example.json` to `runner.json`.

Check platform and telemetry support:

    xemu-test-runner doctor

Start the foreground runner:

    xemu-test-runner run

Process the current queue and exit when it becomes empty:

    xemu-test-runner run --once

Inspect queue counts locally:

    xemu-test-runner queue

Query a running runner from another process or LAN machine:

    xemu-test-runner status --url http://127.0.0.1:9368

## Job package

`job.json` describes how to start the executable contained in the package and what automated actions to perform.

Example Windows package:

    build-123/
    |-- job.json
    |-- xemu.exe
    `-- xemu.toml

Example Linux package:

    build-123/
    |-- job.json
    |-- xemu
    `-- xemu.toml

The Linux executable bit must be preserved.

Example `job.json`:

    {
      "Id": "xemu-smoke-example",
      "Executable": "xemu.exe",
      "Arguments": [
        "-config_path",
        "xemu.toml"
      ],
      "WorkingDirectory": ".",
      "Environment": {},
      "TimeoutSeconds": 120,
      "Tags": ["smoke"],
      "Plan": [
        {
          "Type": "wait",
          "DelayMs": 3000
        },
        {
          "Type": "button",
          "Button": "Start",
          "DurationMs": 100
        },
        {
          "Type": "wait",
          "DelayMs": 500
        },
        {
          "Type": "screenshot",
          "Name": "after-start"
        }
      ]
    }

`Executable` and `WorkingDirectory` are package-relative. Absolute executable paths and paths that escape the package are rejected.

For Linux, set:

    "Executable": "xemu"

The current plan step types are:

- `wait` — wait for `DelayMs`.
- `button` — press a logical Xbox controller button for `DurationMs`.
- `screenshot` — capture the current emulated display to the run result.

The plan does not decide when xemu exits. It performs its actions while xemu continues running; normal process exit or `TimeoutSeconds` ends the test.

## xemu control

The runner owns a local QMP endpoint for each active xemu process. When xemu is launched, the runner appends an argument equivalent to:

    -qmp tcp:127.0.0.1:<automatic-port>,server=on,wait=off

Do not add a second `-qmp` argument to the job while `XemuControl.Enabled` is true.

QMP is currently used for xemu-native control operations such as screenshots and status checks.

### Xbox controller button presses

Controller actions intentionally **do not use QMP `send-key`**.

xemu's keyboard-as-Xbox-controller implementation reads the host SDL keyboard state and maps that state into its Xbox `ControllerState`. QEMU guest-keyboard injection is a different input path.

For deterministic automated input, the package should provide an `xemu.toml` that binds the keyboard to controller port 1:

    [general]
    show_welcome = false
    skip_boot_anim = true

    [general.updates]
    check = false

    [input]
    auto_bind = false

    [input.bindings]
    port1 = 'keyboard'
    port1_driver = 'usb-xbox-gamepad'

The runner then converts logical actions such as:

    A
    B
    X
    Y
    Start
    Back
    White
    Black
    DPadUp
    DPadDown
    DPadLeft
    DPadRight
    LStickUp
    LStickDown
    LStickLeft
    LStickRight
    LTrigger
    RStickUp
    RStickDown
    RStickLeft
    RStickRight
    RTrigger

into the configured host key mapping.

The mapping lives under `XemuControl.ButtonKeys` and can be overridden if the package uses a custom xemu keyboard map.

Current host input providers:

- **Windows:** focuses the active xemu window and injects hardware scan codes with Win32 `SendInput`.
- **Linux X11/XWayland:** finds the xemu X11 window by PID, focuses it, and injects key events with XTest.
- **Native Wayland:** not implemented yet. Use an X11/XWayland xemu session or add a Wayland-specific provider.

Windows can restrict which process is allowed to force foreground focus. The provider makes a best-effort foreground request before each automated button press; this is something the test-box validation needs to exercise.

## Screenshots

Screenshots use xemu/QMP's native `screendump` command with PNG output. This captures the emulated display surface instead of taking an operating-system screenshot of the xemu window.

A planned screenshot is stored under:

    Results/<run-id>/screenshots/

A screenshot can also be requested on demand while a test is active:

    GET /api/v1/screenshot

Example:

    curl http://127.0.0.1:9368/api/v1/screenshot -o current.png

Optionally name the retained result:

    curl "http://127.0.0.1:9368/api/v1/screenshot?name=before-menu" -o current.png

The HTTP response body is the PNG itself with `Content-Type: image/png`.

## On-demand controller API

Press a logical Xbox button on the active xemu test:

    POST /api/v1/input/press

Example:

    curl -X POST       -H "Content-Type: application/json"       -d "{"Button":"A","DurationMs":100}"       http://127.0.0.1:9368/api/v1/input/press

This uses the same input path as `button` steps in `job.json`.

## Telemetry

The default monitoring interval is 100 ms and is configurable in `runner.json`.

The sampler records, when the current platform/provider exposes the value:

- host CPU utilization
- xemu process CPU in core-percent; roughly 100% is one fully occupied logical processor
- host total/used/available memory
- process working set and private memory
- Linux swap bytes
- Windows page-file usage percentage
- process disk read/write throughput
- GPU utilization
- process GPU utilization on Windows
- VRAM total/used where exposed
- process dedicated VRAM on Windows
- GPU temperature and power through NVIDIA NVML
- collector duration and interval overruns

Sampling is single-threaded and non-overlapping. Metrics are placed into a bounded in-memory channel and written by a separate buffered writer so slow disk writes do not stall metric collection.

HTTP status reads the latest cached sample. Polling the API every 100 ms does not cause another CPU/GPU collection.

Raw per-run samples are stored in:

    Results/<run-id>/metrics.csv

The result also records sample count, collection overruns, and dropped CSV-write samples so the overhead of a 100 ms interval can be measured rather than assumed.

## HTTP API

The endpoint exists only while `xemu-test-runner run` is active. It is a small HTTP/1.1 listener embedded directly in the foreground CLI and is intended for a trusted LAN.

Default endpoint:

    http://0.0.0.0:9368

Current control/status routes:

    GET  /api/v1/health
    GET  /api/v1/status
    GET  /api/v1/metrics/latest
    GET  /api/v1/queue
    GET  /api/v1/screenshot
    POST /api/v1/input/press
    POST /api/v1/runner/stop

`POST /api/v1/jobs` is intentionally not a JSON enqueue API. A real queue item must include the executable and job plan together. Complete packages should be staged in `Queue/Pending`.

HTTPS is planned as a transport upgrade; LAN HTTP is the current target.

### Large file upload/download

The general file API remains available for moving large artifacts:

    GET  /api/v1/files/<path>
    HEAD /api/v1/files/<path>
    PUT  /api/v1/files/<path>
    POST /api/v1/files/<path>

Uploads and downloads are streamed and use 64-bit lengths; there is no 10 GB application limit.

Upload:

    curl --upload-file large.iso       http://127.0.0.1:9368/api/v1/files/images/large.iso

Download:

    curl -o large.iso       http://127.0.0.1:9368/api/v1/files/images/large.iso

Range downloads are supported. Resumable sequential uploads use `Content-Range`. Upload progress can be queried with:

    curl "http://127.0.0.1:9368/api/v1/files/images/large.iso?upload-status=1"

See `docs/ARCHITECTURE.md` for transfer and recovery details.

## Current GPU providers

- NVIDIA: NVML loaded directly from the installed driver on Windows or Linux.
- Windows: cached GPU Engine and GPU Process Memory performance counters for the active test process.
- Linux: DRM sysfs values such as `gpu_busy_percent` and VRAM counters when the driver exposes them.

Providers are optional and can be combined. For example, NVML can supply physical GPU/VRAM/temperature information while Windows counters provide xemu-specific GPU and dedicated-memory usage.

## Planned

- HTTPS transport option without changing the route model
- native Wayland input provider
- richer AMD/Intel GPU providers where OS counters are insufficient
- per-thread xemu CPU telemetry
- richer job-plan assertions and branching
- result comparison/report generation
- optional adapter for a future physical Original Xbox over IP
