# Architecture

## Foreground composition

`run` owns the process supervisor, queue, telemetry sampler, Spectre dashboard, control manager and embedded `TcpListener` HTTP endpoint. It is not a service. One host codebase targets Windows and Linux; physical Xbox integration remains out of scope.

The source is split into `Queue`, `Runtime`, `Control`, `Monitoring`, `Networking` and `Reliability`. No database is required. HTTP consumers use cached telemetry; their polling does not query hardware directly.

## Package execution and recovery

```text
exclusive workspace + Testing lease
  -> inspect interrupted journals
  -> move one complete Pending package into Testing
  -> persist attempt (preflight)
  -> validate and write preflight/launch evidence
  -> persist starting before Process.Start
  -> persist PID + process start time
  -> supervise process, plan, watchdog, logs and telemetry
  -> bounded cleanup
  -> persist result
  -> finalize attempt
  -> move whole package into Tested
```

The candidate executable stays inside its package. Renames assume the queue states are on the same local filesystem. Dot-prefixed staging directories are ignored. The Testing directory blocks further claims even when a damaged package is missing job.json.

The journal distinguishes launch uncertainty from an exited attempt. A crash after process creation but before identity persistence leaves `starting`; it is held rather than retried blindly. PID plus start time prevents treating a reused PID as the old xemu. A finalized result found before queue archival is archived without replay. Known exited interruptions are retryable only within the configured bound. Process cleanup failure is not a terminal result eligible for automatic archival.

`AtomicJson` writes a same-directory temporary, flushes it and renames it. A retained lock pathname avoids Unix inode-replacement ownership races. These mechanisms do not promise power-loss durability on every filesystem or correctness on arbitrary network mounts.

## Preflight

The same `Preflight.CheckAsync` is called by `validate` and the execution engine. It validates declared OS, packaged executable/hash, Linux permission, working directory, required files and free space on the results volume. SHA256 is streamed before launch. It does not detect every incompatible binary, inspect PE/ELF architecture, parse xemu firmware config, or freeze arbitrary external files.

## Control and watchdog

The runner appends a local QMP endpoint. `XemuQmpClient` serializes command connections, performs greeting/capability negotiation and honors cancellation/timeouts. The control manager keeps the existing screenshot and pause/resume behavior.

`ResponsivenessWatchdog` is independent of the job timeout: startup grace, periodic query-status, a per-probe deadline and a consecutive-failure threshold. It reports `unresponsive` as a QMP observation, not a rendering/game-progress assertion. A paused VM still has a responsive control plane. Explicit runner cancellation is not a watchdog failure.

Input remains logical button -> configured host key -> Windows SendInput or Linux X11/XTest -> xemu SDL keyboard controller. QMP send-key does not substitute for that controller path. Native provider lifetime cancellation releases held keys before provider disposal. Foreground rejection is reported instead of knowingly typing into another process. Analog controls, native Wayland and Xbox hardware adapters are not implemented.

## Evidence and quality

Per-attempt result directories contain the input job, preflight report, exact launch arguments and executable/job hashes, stdout/stderr, metrics, result, screenshots where available, recorder outputs and `operator-events.jsonl`.

The evidence catalog bounds run listings, artifact enumeration and log tails. Large artifact bodies stream from file handles using 64-bit single-range offsets. Path checks exclude traversal and linked evidence paths. Tails may begin within a UTF-8 character; the byte offset is reported and replacement decoding is intentional.

`ActivityHub` attaches the current run journal and tracks in-flight large transfers. Preview generation, manual input, pause, retained screenshot and bulk transfer events count as intervention. Activity is retained in the result and exposed at `/api/v1/quality`. A run without interventions is still `not_evaluated`: there is no automatic correctness or clean-benchmark verdict.

## Preview and HTTP

`/` is the home dashboard, `/control` the interactive console and `/results` the evidence browser. Static pages have no external framework/CDN dependency. Browser fetch loops are sequential; hidden tabs do not request previews.

One `PreviewCache` coalesces concurrent preview callers. Frames and failure-backoff state are scoped by run ID and a configured minimum interval. A capture is checked against the active run before/after generation and before return. Transient PNG files are removed after being read into the bounded shared cache. Returning a cached frame causes no new QMP operation. Manual retained screenshots stay separate.

The foreground HTTP endpoint retains the existing Content-Length based streamed transfer protocol. This change does not add an upload archive format, automatic queue submission, authentication, HTTPS, WebRTC or a permanent video encoder. HTTPS and higher-rate capture remain separate future work.

## Validation boundary

See [VALIDATION.md](VALIDATION.md). Offline JavaScript/browser fixtures were run, but .NET compilation, the regression executable and native xemu/GPU/input qualification were not run in the authoring environment. The change is not yet a certified unattended test-box build.
