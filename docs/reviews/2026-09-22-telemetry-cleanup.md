# Telemetry cleanup — bounded review

Base reviewed: `684a5829ad8421e1d093777e4c835171b8f514db`.

## Scope

This pass reviews telemetry sampling/recording and the NVML/Windows GPU adapters. It changes only three production files:

- `Monitoring/MetricRecordingSession.cs`
- `Monitoring/Providers/NvidiaNvmlProvider.cs`
- `Monitoring/Providers/WindowsGpuPerformanceCounterProvider.cs`

The lifetime collector, system CPU/RAM provider, CSV writer, and surrounding contracts were read for context. No queue, runtime orchestration, HTTP, UI, diagnostic, experiment, test, dependency, or workflow files are changed. This is deliberately well below half of the application.

## Findings addressed

### Failed NVML refreshes left successful old values in the cache

Previously a non-success status left the cached utilization, memory, temperature, or power untouched. The enclosing metric sample could therefore present an old reading indefinitely after a driver/sensor failure.

Each scheduled refresh now replaces its value with either the successful reading or null. Other valid fields remain usable, and a later successful refresh restores the value. There is no new native polling loop. Memory moves to `SensorIntervalMs`, alongside thermal/power reads, rather than running at the utilization cadence. Null is unavailable; it is not GPU zero and is not a new per-field freshness schema.

### NVML ownership had separate and incomplete cleanup paths

Initialization, device selection, and native library ownership now have one success handoff and one failure cleanup path. A successfully initialized session is shut down before its library is unloaded if provider construction fails. Dispose is idempotent, and Sample rejects calls after disposal instead of invoking freed delegates.

### Windows target changes inherited sampling deadlines

Changing the target already cleared process caches/handles but retained the previous process's rate and memory deadlines. Target-specific deadlines now reset so new rate counters receive their own priming interval. Host counter baselines are retained.

### Counter failure handling missed native/permission exceptions

Windows discovery, priming, and reads now share one explicit expected-failure predicate, including Win32 and security exceptions. Discovery failures continue through the existing backoff rather than escaping before the deadline is updated. A counter opened but not transferred to its dictionary is disposed in finally. Empty-discovery backoff is saturated instead of accumulating indefinitely.

## Readability changes

The recording session uses descriptive observed/written/dropped counters, a named producer-statistics helper, and an explicit writer callback. Comments explain ownership, locking, nonblocking backpressure, and why a completed writer is required before a summary is valid. The existing three-way split (collector / recording session / CSV writer) is retained.

NVML refresh responsibilities, units, delegates, and struct layouts are expanded into normal readable methods/declarations rather than compressed one-line control flow. Public signatures, result property names, CSV columns, and configured defaults remain unchanged.

## Validation boundary

No new tests are added in this cleanup pass. Local .NET compilation is unavailable in the authoring container; existing repository CI results should be read from the PR for the exact published commit. Real NVIDIA driver failure/recovery and Windows GPU target-transition behavior still require native qualification. No hardware-overhead or frame-time improvement is claimed.

## Deferred within telemetry

A full per-field source/acquisition-time/freshness contract remains separate work. Missing NVML values may be filled by another configured GPU provider; adapter-identity consistency across providers is not changed here. Collector duration remains provider wall time, not total runner overhead. CSV flush scheduling and broader writer/consumer architecture are intentionally untouched.
