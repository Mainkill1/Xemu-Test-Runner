# Health API

`GET /health` is the stable compatibility health endpoint. `GET /api/v1/health`
is the versioned alias and returns the same schema. `HEAD` is supported on
both routes. Health is always a small, side-effect-free projection of state
already held in memory; polling it does not scan directories, read result
evidence, probe diagnostic tools, sample hardware, or interact with xemu.

## Contract

The response is schema version 1 and includes:

- `service`, runner `version`, `apiVersion`, and per-process `instance`.
- `timestampUtc`, `startedUtc`, and `uptimeMs`.
- host OS/architecture.
- `status`, `ready`, and `acceptingRequests`.
- current runner `phase` and a small active job/run identity when present.
- Pending/Testing/Tested counts and whether a queue issue currently exists.
- a compact queue-issue code/disposition without its potentially large message.
- the last job/result, aggregate finished/failed counts, and links to deeper APIs.

A typical idle response is:

```json
{
  "schemaVersion": 1,
  "service": "xemu-test-runner",
  "version": "0.1.0+...",
  "apiVersion": "v1",
  "instance": "5abf...",
  "status": "ok",
  "ready": true,
  "acceptingRequests": true,
  "timestampUtc": "2026-09-24T07:00:00Z",
  "startedUtc": "2026-09-24T06:00:00Z",
  "uptimeMs": 3600000,
  "platform": {"os":"windows","architecture":"x64"},
  "phase": "idle",
  "active": null,
  "queue": {"pending":0,"testing":0,"tested":10,"blocked":false},
  "issue": null,
  "last": {"jobId":"build-149-t001","result":"completed","finishedUtc":"2026-09-24T06:59:00Z"},
  "stats": {"jobsFinished":10,"failedJobs":1},
  "checks": {"listener":"ok","queue":"ok"},
  "links": {
    "status":"/api/v1/status",
    "agent":"/api/v1/agent?view=summary",
    "queue":"/api/v1/queue"
  }
}
```

## Semantics

HTTP 200 means the embedded HTTP service is alive and able to answer the
request. A normal active test is still `status=ok` and `ready=true`; health
must not treat useful work as illness.

`status=degraded` and `ready=false` mean the runner is still reachable but
is starting or currently has a queue issue requiring observation/recovery.
The health route remains HTTP 200 so liveness monitors do not restart a runner
merely because a package is blocked. Use `/api/v1/status` for the full issue
message/workstation/telemetry/process state and agent discovery for supported
operations.

`acceptingRequests=true` is intentionally separate from readiness: a degraded
runner can still accept API requests used to inspect or repair its state.

The response has `Cache-Control: no-store`. HEAD returns the same status and
representation headers without a response body.

## What health does not mean

Health does not assert that the current game is correct, that a benchmark is
comparison-eligible, that crash capture is configured, that disk space is
sufficient for the next test, or that external driver/OS state is controlled.
Those checks belong to preflight, run assessment, crash readiness, and the
state ledger rather than a frequently polled liveness endpoint.
