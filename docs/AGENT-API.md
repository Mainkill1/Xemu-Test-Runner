# Agent HTTP API

Normal operation stays on the tester's HTTP interface. The optional Python client runs on the agent/build machine and implements this protocol; it does not execute commands on the tester. Start with [client commands](AGENT-CLIENT.md) for the short workflow.

The foreground runner must already be running persistently. Initial installation/startup, machine recovery and upgrades are operator tasks. Do not use the runner's local --once/--one-shot modes for a persistent remote queue. Do not bypass API benchmark-policy refusals through SSH.

## Discover only what is needed

`GET /api/v1/agent?view=summary` returns the deployed build identity, capabilities, phase/policy information and focused help links. The complete legacy catalog remains at `/api/v1/agent`; fetch it only for explicit reference. `/api/v1/openapi.json` describes the original package workflow, not every additive route or JobDefinition property.

The normal pre-baked workflow is:

```text
choose test ID + immutable revision
  -> create new attempt by reference
  -> copy/verify unchanged payload on tester
  -> upload changed build files only
  -> submit
  -> observe compact status
  -> read compact assessment
  -> inspect selected evidence when needed
```

See [test definitions](AGENT-TEST-LIBRARY.md), [observations](AGENT-OBSERVATIONS.md) and [focused evidence](AGENT-EVIDENCE.md). An unknown/missing deployed capability is an upgrade/configuration issue, not permission to substitute shell access.

## Initial/custom package lifecycle

For a new test definition, create a complete API-owned draft:

```http
POST /api/v1/jobs
Content-Type: application/json
Content-Length: <encoded JSON byte count>
```

```json
{
  "id":"seed-smoke",
  "job":{
    "id":"seed-smoke",
    "targetOs":"windows",
    "executable":"xemu.exe",
    "arguments":["-config_path","xemu.toml"],
    "requiredFiles":["xemu.toml"],
    "timeoutSeconds":120
  },
  "files":[
    {"path":"xemu.exe","length":123,"sha256":"<actual whole-file SHA-256>","executable":true},
    {"path":"xemu.toml","length":456,"sha256":"<actual whole-file SHA-256>"}
  ]
}
```

The example sizes/digests are placeholders. The client computes actual declarations. Job IDs contain 1..64 lowercase letters, digits or hyphens and start with a letter/digit. The JobDefinition ID must match; an omitted/empty ID is filled from the request.

Creation declares 1..4096 files with non-negative 64-bit lengths and whole-file SHA-256 hashes. JSON request bodies are limited to 1 MiB. Send the plan as `job`, not as a payload named job.json. Include every required binary, dependency, workload and runtime-state seed. Paths are package-relative forward-slash paths; hidden/traversal/linked/platform-reserved components are rejected.

A draft is hidden from the queue. Creation does not start xemu. Retrying the same ID and original creation document returns the existing job; a different document conflicts. File declarations are immutable. New build contents need a new manifest/ID or a new pre-baked attempt, not an in-place rewrite of an active package.

## Stream or resume payloads

```http
PUT /api/v1/jobs/seed-smoke/files/xemu.exe
Content-Type: application/octet-stream
Content-Length: <body byte count>
```

The declaration already pins the required digest. Optional `X-Content-SHA256` must match that whole-file digest. The server verifies content before completed-file publication.

For large files, send sequential chunks:

```http
PUT /api/v1/jobs/seed-smoke/files/test.iso
Content-Length: 8388608
Content-Range: bytes 0-8388607/12884901888
X-Content-SHA256: <whole-file digest>
```

An incomplete response is 202 with uploadId, received/total and complete:false. Later chunks include `X-Upload-Id`. Ranges are inclusive; Content-Length is the current chunk length, not the final file length. Zero-length whole-file uploads are supported.

After a disconnect, query:

```http
GET /api/v1/jobs/seed-smoke/files/test.iso?upload-status=1
```

Resume at the returned committed `length`, not the client's last attempted byte count. `publication_unconfirmed` does not prove a completed file. Never manufacture a zero-length final range to convert it into success.

Payloads stream with bounded buffers and 64-bit sizes. HTTP chunked request encoding is not supported; Content-Length is required. There is no application-wide 10 GB cap, but filesystem/capacity limits remain. Real 10 GB+ LAN qualification is separate from this protocol's arithmetic.

Only one mutation owns a draft at a time. Concurrent upload/edit/submit returns job_busy. `GET /api/v1/jobs/{id}/files` is an explicit full declaration/status inspection, not a routine polling call. Completed payload GET/HEAD supports single byte ranges at the same file URL.

## Edit, validate and submit

`GET /api/v1/jobs/{id}?view=summary` is the ordinary observation. Fetch `GET /api/v1/jobs/{id}` only when the full plan, declaration list, legal actions or quoted plan revision is needed.

Replace a draft plan with:

```http
PUT /api/v1/jobs/seed-smoke/plan
If-Match: "<current plan revision>"
Content-Type: application/json
```

The body is the complete JobDefinition, not JSON Patch. Missing If-Match returns 428; a stale revision returns 412. Job ID cannot change. No live plan or completed evidence is editable.

```http
POST /api/v1/jobs/seed-smoke/validate
POST /api/v1/jobs/seed-smoke/submit
```

These parameterless actions accept an empty body or `{}`. Unexpected JSON fields are rejected before mutation. Empty JSON is consumed before the response closes, avoiding an unread request body on successful action responses.

Validation/submission returns 202, an operation Location and Retry-After. The operation belongs to the runner and survives the requesting connection. Poll its receipt, not a second creation request:

```http
GET /api/v1/jobs/seed-smoke/operation
GET /api/v1/jobs/seed-smoke/validation
```

Queued/running operations are nonterminal; completed/failed/interrupted are terminal. Submission verifies all declared file lengths/hashes/references and existing preflight, then atomically publishes the complete package to Pending. The existing runner automatically claims it: no separate remote CLI launch is needed.

Acceptance, preflight and queue publication are not correctness verdicts. The normal execution engine still owns launch/control/readiness/timeout/finalization. A request that loses its response may already have succeeded; retain the same job ID and inspect its state.

## Observe outcomes, not raw files

Summary views expose lifecycle cursors independent of plan revisions, bounded waits, held/blocked states and canonical assessment outcomes. Missing/invalid assessment data never implies pass.

```http
GET /api/v1/jobs/seed-smoke?view=summary
GET /api/v1/runs/<run-id>?view=summary
```

Execution, correctness, evidence and comparison remain independent. `tested` is archival queue disposition, not guest correctness. The older `GET /api/v1/runs/{id}` remains an artifact listing; use the summary view for assessment information.

Detailed evidence remains available through the ranged artifact route, paged artifact metadata and bounded incremental logs. The client defaults to an assessment summary and selected collection, not every file. `collect --all` is explicit and must check every page's completeness and every download. Artifact byte counts are checked; no independent result digest manifest is currently provided.

Existing target controls and diagnostics remain available under `/api/v1/control`, `/api/v1/xemu/*`, `/api/v1/input/press` and `/api/v1/diagnostics`. Their operation policies still apply. Experiment aggregation remains available at `/api/v1/experiments/{id}`; it is not an automatic statistical-significance or Xbox-correctness claim.

## Withdraw, cancel or repeat

`POST /api/v1/jobs/{id}/withdraw` atomically returns an unclaimed queued job to draft. Queue claim and withdrawal compete through rename; a lost claim race returns conflict, not permission to edit Testing.

`DELETE /api/v1/jobs/{id}` cancels only draft/unclaimed work and retains its files. It does not delete evidence or kill an active process.

`POST /api/v1/jobs/{id}/clone` with `{"newId":"new-attempt"}` copies a stable package into a new draft. Poll the destination operation, edit if needed, then submit. For a new build, use the test-library reference workflow or a new manifest plus `/reuse` so unchanged workload assets need not cross the network again.

Existing manually staged packages are not automatically adopted by the API-owned draft store. Do not rename its hidden directories or manipulate its journals outside the API.

## Error recovery

Errors retain machine-readable code, status, explanation and a correction hint. job_busy/upload_in_progress means inspect the same operation or upload status. Revision mismatches require refreshing the plan revision before an intentional edit. preflight_failed requires reading validation and correcting the draft. operation_blocked means respect the active benchmark policy. Missing/held ownership is not evidence that a new attempt is safe.

Hashing/copying/transfers retain existing activity accounting across run transitions; this is not a scheduler-level zero-overlap guarantee. The listener remains trusted-LAN-only without built-in authentication/TLS. Keep the foreground runner available instead of restarting it for every agent request.

## Checks

```sh
dotnet run --project tests/AgentChecks -c Release
python -m unittest discover -s tests -p 'test_runner_api*.py' -v
dotnet run --project tests/AgentChecks -c Release -- --client
```

The final command runs the Python client against the real embedded listener using fixture packages. It does not launch real xemu. Windows/Linux native rendering, large LAN transfers, runtime recovery and monitoring overhead still need the test rigs. PR verification records preserve the earlier intermittent Windows filesystem-access finding rather than treating later green runs as proof of its resolution.
