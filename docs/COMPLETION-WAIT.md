# Wait for completion without repeated status dumps

Use the same test ID to wait quietly until it finishes, or opt into small periodic updates. These are read-only operations: waiting never authorizes execution, repeats an attempt, changes a test timeout, cancels the target, or downloads artifacts.

## Client

```sh
# Quiet until finished or an attention-needed condition. No overall wait limit.
python scripts/runner_tests.py wait build-149-t001 --follow

# Optional short JSON updates, at most once per held-request interval.
python scripts/runner_tests.py wait build-149-t001 --follow --updates --interval 20

# Return the final result or current state within a bounded wait window.
python scripts/runner_tests.py wait build-149-t001 --max-wait 60

# An ordinary /jobs ID rather than a /test-runs selection.
python scripts/runner_tests.py wait custom-job --job --follow
```

Keep runner_wait.py alongside runner_tests.py and its existing helper modules. Default behavior waits for up to 30 seconds and prints one compact JSON reply; `--max-wait 0` performs an immediate observation. `--follow` repeats bounded HTTP waits silently until finished or attention is required. `--updates` prints newline-delimited JSON heartbeats on stdout, flushed as they arrive, followed by the final response. Stderr is not a hidden heartbeat channel.

Exit zero means the read succeeded, not that the test passed. Read the final assessment; crashes remain crashes. The existing runner_api.py correctness/eligibility gates remain available. Unknown/malformed replies or missing capabilities produce a structured error rather than falling back to SSH or submitting another attempt.

## API

```http
GET /api/v1/test-runs/{id}/wait?wait=20
GET /api/v1/jobs/{id}/wait?wait=20
```

The first path works even when a selected test is queued and its executable package has not yet been materialized. The second is for ordinary API-owned jobs. IDs retain their existing meaning; the API does not guess a namespace or create a missing ID.

Each GET holds one response until completion, an attention-needed condition, or the requested interval. `wait` is an integer from 0 through 120 seconds, default 20. `0` returns immediately. Nonterminal state changes do not release a completion waiter early; lifecycle-change observation remains a separate unchanged API.

A heartbeat is one small HTTP 200 JSON response, not an SSE/WebSocket stream:

```json
{"ok":true,"id":"build-149-t001","state":"testing","event":"heartbeat","terminal":false,"runId":"run-123","code":null,"next":"/api/v1/test-runs/build-149-t001/wait?wait=20","result":null}
```

Repeat the next read after a heartbeat. Finished work returns `event:finished`, `terminal:true`, and its compact canonical assessment in `result` when available. Test archival, failed preparation and cancellation are terminal; neither HTTP 200 nor terminal means guest correctness. Missing/invalid assessment evidence never becomes a passing result.

Unstarted uploads/drafts, missing application uploads, held targets, queue issues and unavailable ownership return `event:attention`, an explicit code and a detail URL without an indefinite wait. In particular, waiting on an uploaded selection returns `start_required`; it does not start the test. Resolve the condition deliberately before waiting again.

## Bounds and ownership

This uses existing job observations, small requested-test/attempt journals and the cached assessment reader. It does not hash payloads, read raw CSV/logs, enumerate evidence or interact with the target. It remains available during benchmarks. The server checks completion at most twice per second per waiter, using asynchronous delays; polling overhead is not claimed to be zero.

The existing maximum of 32 waiting requests is shared with lifecycle-change long polls. Additional waits return HTTP 429/too_many_waiters. Immediately completed or attention-needed responses and ordinary status requests do not require a waiter slot. A disconnected client may retain its slot until the bounded interval expires; server shutdown cancels pending waits. None of those conditions changes the target's ownership or cancels execution.

No single network connection is held forever. `--follow` obtains the quiet-until-finished experience by repeating finite requests with the same ID. The client socket timeout exceeds each server wait; intermediary proxy limits may require a shorter --interval. Network failure reports an error without replaying mutations. Reissue the same read, not a new test.

Discovery advertises `completionWait` and `waitHelp`; focused details are at `GET /api/v1/help?topic=completion-wait`.

## Verification

The HTTP fixtures exercise a held response, heartbeat timing/budget, completion with crash assessment, queue-before-materialization, cancellation, unstarted/held/blocked handling, missing evidence and invalid IDs/limits. Python subprocess fixtures verify quiet follow, opt-in updates, a bounded immediate read, explicit job namespace, capability refusal and read-only traffic. Existing run execution and diagnostic collection are unchanged.
