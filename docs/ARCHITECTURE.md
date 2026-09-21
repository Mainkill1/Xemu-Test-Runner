# Architecture

## Scope

Xemu Test Runner is host-side only. It does not currently contain Original Xbox code and does not assume that an Xbox is available.

The current executable is a foreground CLI application. It is not a Windows Service, systemd unit, ASP.NET application, or generic-host daemon. Running `xemu-test-runner run` owns the queue, telemetry sampler, target process, result capture, and embedded LAN HTTP endpoint for the lifetime of that foreground command.

A later hardware adapter may talk to a real Xbox over HTTP without changing the host queue or telemetry model.

## Queue state

Only small JSON job descriptors move between queue directories:

    Pending -> Testing -> Tested

The executable or ISO referenced by a job stays in place. This avoids copying large payloads and makes the queue transition an inexpensive same-filesystem rename.

Only one JSON job may be in `Testing` at a time. If the runner terminates unexpectedly, that file remains there. On the next start:

- `InterruptedAction = retry` records a recovery event and moves the descriptor back to Pending.
- `InterruptedAction = hold` leaves it in Testing and the queue does not start another job.

A normal process failure, timeout, or non-zero exit is still a completed test attempt and therefore moves to Tested with the outcome in `result.json`.

## Monitoring

One sampler owns host/process telemetry. API requests never query OS or GPU counters directly.

At the configured `Monitoring.IntervalMs` cadence the sampler collects a snapshot, publishes the newest snapshot in memory, and tries to place it in a bounded channel. A separate writer drains that channel to `metrics.csv` and flushes at `Monitoring.FlushIntervalMs`.

The sampler is sequential. If one sample costs more than its configured interval, it is marked as an overrun and the next collection starts afterward. The runner never creates overlapping metric collection tasks to catch up.

Process CPU is recorded in core-percent. Roughly 100% means one fully occupied logical processor; a multithreaded xemu process can exceed 100%.

Current metric sources:

- Windows: GetSystemTimes, GlobalMemoryStatusEx, GetProcessIoCounters, persistent PerformanceCounter objects for page-file percentage and process GPU counters.
- Linux: `/proc/stat`, `/proc/meminfo`, `/proc/<pid>/io`, DRM sysfs where exposed.
- NVIDIA Windows/Linux: NVML loaded dynamically from the installed driver.

GPU providers are optional. Missing vendor support does not prevent a test from running.

## Embedded HTTP

The runner implements a small HTTP/1.1 endpoint directly over `TcpListener`. It intentionally does not use ASP.NET Core or a service host.

Current routes:

    GET  /api/v1/health
    GET  /api/v1/status
    GET  /api/v1/metrics/latest
    GET  /api/v1/queue
    POST /api/v1/jobs
    POST /api/v1/runner/stop
    GET  /api/v1/files/<path>
    HEAD /api/v1/files/<path>
    PUT  /api/v1/files/<path>
    POST /api/v1/files/<path>

HTTP is intended for a trusted LAN. Authentication is intentionally absent in the first implementation. HTTPS is a planned transport upgrade.

### Large transfers

File request bodies are streamed directly between the socket and disk using a rented buffer sized by `Http.TransferBufferBytes`. File sizes and transfer counters use 64-bit lengths.

Downloads support the standard `Range: bytes=...` header.

Uploads support two modes:

1. A normal PUT/POST with Content-Length writes to `<target>.uploading` and atomically replaces the final path after all bytes arrive.
2. A resumable PUT/POST with `Content-Range: bytes start-end/total` writes sequentially to `<target>.partial`. The requested start must equal the existing partial length. When `end + 1 == total`, the partial file becomes the final file.

Query upload progress with:

    GET /api/v1/files/<path>?upload-status=1

Chunked request encoding is deliberately not implemented yet. Clients should send Content-Length.

## Future real-Xbox support

The future Xbox integration should be an adapter beside the existing process runner, not a replacement for it. The host should continue to own queue state, host performance data, artifacts, and external HTTP. A hardware adapter can later add commands and state for an Xbox at a configured IP while preserving the same result model.
