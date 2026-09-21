# Architecture

## Scope

Xemu Test Runner is currently a host-side Windows/Linux application. It does not contain Original Xbox code and does not require physical Xbox hardware.

The runner is a foreground CLI application. It is not a Windows Service, systemd unit, ASP.NET application, or Generic Host daemon.

Running:

    xemu-test-runner run

owns the queue, active xemu process, telemetry sampler, test plan, result capture, and embedded LAN HTTP endpoint for that foreground command.

A later physical-Xbox adapter should plug into the control layer without changing the host queue/result model.

## Queue packages

A queue item is a directory, not a standalone descriptor.

Minimum valid package:

    <job>/
    |-- job.json
    `-- <configured xemu executable>

Typical package:

    build-123/
    |-- job.json
    |-- xemu.exe
    |-- xemu.toml
    `-- test assets...

The configured executable must be inside the package. Absolute executable paths and package-relative paths that escape the package are rejected.

The whole directory moves through:

    Pending -> Testing -> Tested

Only one valid package may occupy Testing at a time.

This gives a queue state a strong meaning:

- Pending: this exact xemu build and this exact test plan have not started.
- Testing: this exact package is or was the active attempt.
- Tested: this exact package was attempted and a result was recorded.

A normal xemu crash, timeout, non-zero exit, or plan failure is still a completed attempt and moves to Tested.

### Crash recovery

If the runner or host terminates unexpectedly, the active package remains in Testing.

On startup:

- `InterruptedAction = retry` records a recovery event and moves the whole package back to Pending.
- `InterruptedAction = hold` leaves it in Testing for inspection.

### Package staging

Queue discovery ignores directories beginning with `.`.

Large packages should therefore be copied as:

    Pending/.incoming-build-123/

and renamed only after all files have arrived:

    .incoming-build-123 -> build-123

When Pending, Testing, and Tested are on the same filesystem, queue transitions are directory renames rather than file copies.

## Job plan

`job.json` has two roles:

1. Define how the package's xemu binary is started.
2. Define optional sequential actions to perform while it runs.

Current plan steps:

- `wait`
- `button`
- `screenshot`

A plan completing does not terminate xemu. The target process exits normally, times out, crashes, is stopped, or is killed because a plan action failed.

## xemu control

The control layer deliberately separates QMP control from Xbox controller input.

### QMP

For each controlled process, the runner allocates a localhost TCP port and appends:

    -qmp tcp:<host>:<port>,server=on,wait=off

The runner owns this endpoint. Jobs must not provide their own `-qmp` argument while xemu control is enabled.

`XemuQmpClient` performs the QMP greeting/capabilities handshake and uses short-lived TCP connections for commands.

Current QMP uses:

- `query-status` to establish that the launched xemu process is control-ready.
- `screendump` with PNG format for screenshots.

The QMP port is recorded in the run result.

### Xbox controller input

xemu's keyboard-as-controller implementation reads SDL host keyboard state and maps it to Xbox `ControllerState`.

This is not the same path as QEMU guest-keyboard input, so QMP `send-key` is intentionally not used for Xbox buttons.

Test packages should bind xemu's keyboard input to Xbox controller port 1. The test plan works with logical names such as `A`, `Start`, and `DPadUp`; `XemuControl.ButtonKeys` maps those names to host keyboard keys.

Current providers:

- Windows: find the visible window owned by the active xemu PID, request foreground focus, inject scan-code key down/up with Win32 `SendInput`.
- Linux X11/XWayland: find the X11 window by `_NET_WM_PID`, focus it, inject key down/up with XTest.
- Native Wayland: not implemented.

Input provider details are hidden behind `IXemuInputProvider` so a future xemu-specific controller RPC, virtual gamepad, Wayland provider, or physical-Xbox adapter can replace host keyboard injection without changing job plans.

## Screenshot flow

Screenshots are produced by xemu itself, not by desktop screen capture.

Flow:

    HTTP or job plan
        -> XemuControlManager
        -> QMP screendump(format=png)
        -> Results/<run-id>/screenshots/<name>.png

For an HTTP request, the newly created PNG is then streamed back as the response body.

This means the screenshot represents the emulated display surface without xemu window borders, terminal windows, or other desktop content.

## Monitoring

One sampler owns host/process telemetry. HTTP requests never query OS or GPU counters directly.

At `Monitoring.IntervalMs` the sampler collects a snapshot, publishes the newest snapshot in memory, and tries to place it into a bounded channel. A separate writer drains that channel to `metrics.csv`.

Collection is sequential and deliberately non-overlapping. If a sample takes longer than its configured interval:

- the sample is marked as an overrun;
- no second collector is started to catch up;
- the next sample begins after the slow sample completes.

The result records sample count, overruns, and dropped CSV samples so the cost of a 100 ms cadence can be measured on each test host.

Process CPU uses core-percent. Roughly 100% means one fully occupied logical processor; a multithreaded xemu process may exceed 100%.

Current sources:

- Windows: GetSystemTimes, GlobalMemoryStatusEx, GetProcessIoCounters, page-file PerformanceCounter data, process GPU counters.
- Linux: `/proc/stat`, `/proc/meminfo`, `/proc/<pid>/io`, DRM sysfs when available.
- NVIDIA Windows/Linux: NVML loaded dynamically from the installed driver.

GPU providers are optional.

## Embedded HTTP

The runner implements a small HTTP/1.1 endpoint directly over `TcpListener`. It intentionally does not use ASP.NET Core or a service host.

Current routes:

    GET  /api/v1/health
    GET  /api/v1/status
    GET  /api/v1/metrics/latest
    GET  /api/v1/queue
    GET  /api/v1/screenshot
    POST /api/v1/input/press
    POST /api/v1/runner/stop
    GET  /api/v1/files/<path>
    HEAD /api/v1/files/<path>
    PUT  /api/v1/files/<path>
    POST /api/v1/files/<path>

`POST /api/v1/jobs` does not accept a JSON-only job because that would separate the plan from the executable. The endpoint returns an explanation directing the caller to stage a complete queue package.

HTTP is intended for a trusted LAN. Authentication is intentionally absent in this phase. HTTPS is a planned transport upgrade.

### Latest metrics

`GET /api/v1/metrics/latest` reads the in-memory latest sample. It never causes another telemetry collection. API polling rate and hardware collection rate are therefore independent.

### On-demand screenshot

`GET /api/v1/screenshot` requires an active xemu session.

The runner:

1. asks the active xemu QMP endpoint for a PNG;
2. retains the PNG under the current result directory;
3. streams that exact file back with `Content-Type: image/png`.

The optional query parameter `name` controls the retained screenshot base name.

### On-demand button

`POST /api/v1/input/press` accepts:

    {
      "Button": "A",
      "DurationMs": 100
    }

It invokes the same logical-button mapping and input provider used by queued plan steps.

## Large transfers

General artifact transfer remains separate from queue semantics.

File bodies are streamed directly between socket and disk through a rented buffer sized by `Http.TransferBufferBytes`. File sizes and transfer counters use 64-bit values.

Downloads support:

    Range: bytes=...

Uploads support:

1. normal PUT/POST with Content-Length, written to `<target>.uploading` and renamed when complete;
2. resumable sequential PUT/POST with `Content-Range: bytes start-end/total`, written to `<target>.partial`.

Upload status:

    GET /api/v1/files/<path>?upload-status=1

Chunked request encoding is deliberately not implemented. Clients should provide Content-Length.

## Results

Each run creates:

    Results/<run-id>/
    |-- job.json
    |-- result.json
    |-- stdout.log
    |-- stderr.log
    |-- metrics.csv
    `-- screenshots/
        `-- ...

The result identifies the queued package, executable SHA-256, process exit state, QMP endpoint, host environment, and telemetry collection health.

The queued build itself remains preserved in Tested with its original job package.

## Future physical Xbox support

A real-Xbox implementation should be another control adapter, not a second runner architecture.

The host should continue to own:

- queue state
- performance monitoring
- result artifacts
- external HTTP API
- test plans

A physical-Xbox adapter can later translate the same logical operations—status, input, screenshots where possible, file movement, and test state—to the hardware transport available at that time.
