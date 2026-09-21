# Xemu Test Runner

Host-side Windows/Linux runner for repeatable xemu testing. It launches one queued test at a time, records host/process performance telemetry, captures logs and results, survives runner crashes without losing queue state, and exposes a small LAN HTTP interface while the foreground CLI is running.

This repository currently targets the host only. No Original Xbox executable is included or required.

## Design

    workspace/
    |-- Queue/
    |   |-- Pending/
    |   |-- Testing/
    |   `-- Tested/
    |-- Results/
    |-- Files/
    `-- Payloads/

Only job JSON moves through Pending -> Testing -> Tested. Test executables remain under Payloads or at an absolute path. Testing therefore always identifies the exact job that was active if the runner or host crashes.

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

Check platform telemetry support:

    xemu-test-runner doctor

Start the foreground runner:

    xemu-test-runner run

Process only the jobs that currently exist and then exit:

    xemu-test-runner run --once

Inspect queue counts locally:

    xemu-test-runner queue

Query a running runner:

    xemu-test-runner status --url http://127.0.0.1:9368

## Job format

Paths that are not absolute are resolved under the configured workspace.

    {
      "Id": "xemu-smoke-example",
      "Executable": "Payloads/xemu/xemu.exe",
      "Arguments": ["-full-screen", "0"],
      "WorkingDirectory": "Payloads/xemu",
      "Environment": {},
      "TimeoutSeconds": 0,
      "Tags": ["example"]
    }

Place job JSON in `workspace/Queue/Pending`, or POST it to `/api/v1/jobs`.

## Telemetry

The default monitoring interval is 100 ms and is fully configurable in `runner.json`.

The sampler currently records, when the platform/provider exposes the value:

- host CPU utilization
- xemu/test process CPU in core-percent (100% is roughly one fully occupied logical processor)
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

Sampling is single-threaded and non-overlapping. Metrics are put into a bounded in-memory channel and written by a separate buffered writer so disk latency does not stall the test process. HTTP status reads the most recent in-memory sample; polling the API does not re-run telemetry collection.

Raw per-run metrics are written to `metrics.csv`.

## HTTP API

The HTTP endpoint exists only while `xemu-test-runner run` is active. It is a small HTTP/1.1 listener embedded directly in the CLI process and is intended for trusted LAN use.

Default endpoint:

    http://0.0.0.0:9368

Useful calls:

    curl http://127.0.0.1:9368/api/v1/status
    curl http://127.0.0.1:9368/api/v1/metrics/latest
    curl http://127.0.0.1:9368/api/v1/queue

Queue a job:

    curl -X POST --data-binary @jobs/example-job.json \
      -H "Content-Type: application/json" \
      http://127.0.0.1:9368/api/v1/jobs

### Large file upload/download

Uploads and downloads are streamed and use 64-bit lengths; there is no 10 GB application limit.

Upload:

    curl --upload-file large.iso \
      http://127.0.0.1:9368/api/v1/files/images/large.iso

Download:

    curl -o large.iso \
      http://127.0.0.1:9368/api/v1/files/images/large.iso

Range downloads are supported. Resumable sequential uploads use `Content-Range`; progress can be queried with:

    curl "http://127.0.0.1:9368/api/v1/files/images/large.iso?upload-status=1"

See `docs/ARCHITECTURE.md` for transfer behavior and queue recovery details.

## Current GPU providers

- NVIDIA: NVML loaded directly from the installed driver on Windows or Linux.
- Windows: cached GPU Engine and GPU Process Memory performance counters for the target process.
- Linux: DRM sysfs values such as `gpu_busy_percent` and VRAM counters when the driver exposes them.

Providers are optional and combined when useful. For example, NVML can provide physical GPU/VRAM/temperature data while Windows counters provide xemu-specific GPU and dedicated-memory usage.

## Planned

- HTTPS transport option without changing the HTTP route model
- richer AMD/Intel GPU providers where current OS counters are insufficient
- per-thread xemu CPU telemetry
- result comparison/report generation
- optional adapter for a future real Original Xbox over IP
