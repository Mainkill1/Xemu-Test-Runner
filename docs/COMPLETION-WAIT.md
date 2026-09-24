# Wait until the test finishes

```sh
python scripts/runner_tests.py wait build-149-t001
python scripts/runner_tests.py wait build-149-t001 --updates
python scripts/runner_tests.py wait custom-job --job
```

The default command stays attached until the test is finished or needs attention. It has **no overall wait deadline**, no duration estimate, and no follow/interval/timer argument. Agents do not need to know how long a workload or its queue should take. Only `--updates` (show short progress replies) and `--job` (ordinary API job namespace) are optional.

Normal stdout is one final/attention JSON document. `--updates` explicitly prints newline-delimited JSON heartbeats as well. Transient connection errors and HTTP 408/429/500/502/503/504 automatically retry the same read with bounded backoff; no mutation is ever replayed. Invalid identity/schema, missing capabilities, unknown IDs and authorization errors remain visible errors. Ctrl+C ends the observation, not the test.

## Direct API

```http
GET /api/v1/test-runs/{id}/wait
GET /api/v1/jobs/{id}/wait
```

No query parameters are accepted. The runner—not the agent—chooses a short transport heartbeat cadence. A held response ends on `finished`, `attention`, or an automatic `heartbeat`. A direct HTTP consumer follows the same `next` read after a heartbeat for as long as needed. The bundled client does this internally with no overall deadline, hiding heartbeats unless `--updates` was selected.

This is deliberately not one immortal network connection: finite transport reads allow reconnecting without losing test identity, while the logical observation remains open indefinitely. The socket timeout is an internal transport guard and never becomes a test-duration estimate or causes a new attempt.

| Event | Meaning |
| --- | --- |
| heartbeat | Still queued/running; continue the same read automatically. |
| finished | Archived execution, cancelled selection or failed preparation; inspect the canonical assessment. |
| attention | Unstarted upload, held target, blocked queue, missing input or uncertain ownership; stop blind polling and handle the reported condition. |

Waiting is read-only. It never starts, submits, cancels, repeats, extends or changes ownership of a test. An unstarted selection returns start_required. Waiting covers selections still queued before their API job exists. A crash remains crashed; missing assessment evidence never becomes a pass. Exit zero means observation succeeded, not that correctness passed.

The server shares the existing 32 held-request slots with lifecycle observers. It checks only small journals/observations with asynchronous delays, not full plans, payload hashes, live metrics or raw artifacts. Disconnected reads release capacity by their next internal heartbeat or shutdown. Legacy lifecycle-change observations remain separate from this completion contract.

## Verification

HTTP fixtures exercise the automatic heartbeat, completion during a held request, pre-materialization cancellation, blockers, missing evidence and rejection of timer parameters. Python fixtures verify default quiet continuation across several synthetic days, no timer options in help, automatic reconnects, selected update output, and no start/cancel/retry mutations.
