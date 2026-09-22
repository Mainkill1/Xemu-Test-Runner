# Agent operation through HTTP

## Contract

Use the tester's HTTP API for normal remote operation. An agent should not SSH/RDP into the test machine to deposit a build, edit job.json, move queue directories, invoke a test, or copy its evidence back.

The runner is still one foreground C# application with its embedded listener. The optional Spectre display is an operator view, not an automation dependency. Install/configure/start it once on the tester and leave it running:

```text
XemuTestRunner run --non-interactive
```

Do not use `--once` or `--one-shot` for this persistent remote workflow: those deliberately exit when their finite work is finished. Installation, initial launch, remote application upgrades and waking/recovering a powered-off machine remain bootstrap/operator tasks. An API in a stopped process cannot restart itself.

## Start with discovery

```http
GET /api/v1/agent
```

The same discovery document is available at `GET /api/v1` and `GET /.well-known/agent.json`. It contains the workflow, limits, example creation request, supported job operations, existing control/result links and error guidance.

`GET /api/v1/openapi.json` describes the new job workflow in OpenAPI 3.1. This is not a claim that every legacy route or every JobDefinition property has been exhaustively described in OpenAPI. The existing job.json format remains the plan schema.

`GET /api/v1/help` now returns `agent` (the workflow catalog) and `controls` (the existing route/error catalog). New job API responses use camelCase. Existing status/results endpoints retain their established field casing; clients must not assume this change rewrites legacy responses.

Every job response includes named `actions` with method, relative URL and purpose. Follow those actions rather than constructing remote filesystem paths. All returned relative URLs are on the same tester origin.

## The complete workflow

```text
create draft
    -> upload or resume declared files
    -> edit draft plan if needed
    -> validate (optional explicit preview)
    -> submit (always validates before publication)
    -> poll operation
    -> poll job
    -> follow run/result links
    -> download listed artifacts
```

A new attempt uses a new job ID. A retry after a lost HTTP response uses the same ID and the identical original creation document. Never create another attempt merely because an upload/submission request timed out.

### Client convenience commands

Run the bundled standard-library Python client on the **agent/build machine**, not the tester. Python 3.10 or later is required. It performs HTTP calls only; there is no SSH fallback.

```sh
python scripts/runner_api.py --url http://192.168.1.42:9368 discover
python scripts/runner_api.py --url http://192.168.1.42:9368 submit ./build-package --id build-149-a --wait
python scripts/runner_api.py --url http://192.168.1.42:9368 collect build-149-a ./evidence
```

`submit` reads local job.json, computes actual file lengths/digests, creates a draft, uploads files in bounded chunks, submits it, and polls the submission operation. `--wait` additionally waits for a terminal queue disposition. Its exit status is not an assertion that the guest passed; inspect the returned run assessment.

Hidden files/directories are omitted from package discovery, and symlinks are rejected. The Python helper reads ordinary JSON, not the comments/trailing commas accepted by the C# configuration parser. Every required payload must be present in the local package.

Stdout is JSON; progress and errors go to stderr. Network failures can be recovered by rerunning with the **same ID and unchanged local package**. If the server says `job_busy`, poll its existing operation before retrying.

## Create a draft

```http
POST /api/v1/jobs
Content-Type: application/json
Content-Length: <actual encoded byte count>
```

Example shape only: lengths and digest strings below must be replaced with the real values. The Python helper computes them.

```json
{
  "Id": "build-149-a",
  "Job": {
    "Id": "build-149-a",
    "TargetOs": "windows",
    "Executable": "xemu.exe",
    "WorkingDirectory": ".",
    "Arguments": ["-config_path", "xemu.toml"],
    "RequiredFiles": ["xemu.toml"],
    "TimeoutSeconds": 120
  },
  "Files": [
    {"Path": "xemu.exe", "Length": 123, "Sha256": "<64 hexadecimal digits>", "Executable": true},
    {"Path": "xemu.toml", "Length": 456, "Sha256": "<64 hexadecimal digits>"}
  ]
}
```

`Id` is 1..64 lowercase letters, digits or hyphens, starting with a letter/digit. Job.Id must match; an omitted/empty Job.Id is filled with the API ID. Use a stable unique ID such as build/workload/attempt identity.

A creation request declares 1..4096 files. Every file requires a non-negative **64-bit** byte length and the whole-file SHA-256 digest. Executable marks a Linux executable permission bit; it does not execute the file during upload. The plan itself is sent in `Job`, not uploaded as a payload named job.json.

Paths are package-relative forward-slash paths. Hidden components, traversal, symlinks/junctions and reserved Windows device names are rejected. Declare the executable, dependencies, required inputs and runtime-state seeds. File declarations cannot be changed after creation; create another package ID for a changed file set or build. Plan edits are supported independently.

The draft remains hidden from the execution queue until explicit submission. Creating it does not start xemu. Repeating the same original creation document with the same ID returns the existing job; different content with that ID returns `job_identity_conflict`.

## Upload large files

```http
PUT /api/v1/jobs/build-149-a/files/xemu.exe
Content-Length: <body bytes>
Content-Type: application/octet-stream
```

The declared digest is always checked by the upload store before a completed file is published. Supplying `X-Content-SHA256` is optional here because the declaration already pins it; a supplied value must match the declaration.

Use sequential chunks for large files:

```http
PUT /api/v1/jobs/build-149-a/files/test.iso
Content-Length: 8388608
Content-Range: bytes 0-8388607/12884901888
X-Content-SHA256: <whole 12 GiB file digest>
```

An incomplete upload returns 202 with `uploadId`, `received`, `total` and `complete:false`. Later chunks send that ID in `X-Upload-Id`. The range is inclusive and Content-Length is the current body size, not the final size. Do not send a chunk digest as the whole-file digest.

After any disconnect, query:

```http
GET /api/v1/jobs/build-149-a/files/test.iso?upload-status=1
```

Resume from the returned committed `length`, not the client's last attempted byte count. `publication_unconfirmed` is not proof of a completed file. The client helper reports it explicitly rather than inventing a final zero-length range.

Whole-file uploads and zero-length files are supported with Content-Length. Chunked HTTP request encoding is not supported. There is no application-level 10 GB ceiling; filesystem capacity/limits still apply. Payloads stream through a bounded buffer instead of loading the full file into memory.

Only one mutation owns a draft at a time. Overlapping uploads/edits/submission for the same job return `job_busy`; independent drafts can upload independently. Successful partial and final files remain in hidden staging until submission. Completed payload downloads support GET/HEAD and single byte ranges at the same file URL.

`GET /api/v1/jobs/{id}/files` returns declarations, upload state, and upload/status URLs for the package. This is an administration call, not a 100 ms telemetry poll.

## Edit, validate and submit

Fetch the current job to get its quoted `revision` and allowed actions:

```http
GET /api/v1/jobs/build-149-a
```

Replace an editable draft's plan:

```http
PUT /api/v1/jobs/build-149-a/plan
If-Match: "<revision returned by GET>"
Content-Type: application/json
```

The body is the complete JobDefinition document, not JSON Patch. Missing If-Match returns 428; a stale revision returns 412. This prevents an agent from silently overwriting another worker's plan edits. Changing Job.Id is not allowed.

```sh
python scripts/runner_api.py --url http://192.168.1.42:9368 edit build-149-a ./revised-job.json
python scripts/runner_api.py --url http://192.168.1.42:9368 request POST /api/v1/jobs/build-149-a/validate
```

Validation and submission are asynchronous **runner operations**:

```http
POST /api/v1/jobs/build-149-a/validate
POST /api/v1/jobs/build-149-a/submit
```

Both return 202 with a `Location` pointing to the job's operation and `Retry-After: 1`. Poll once per second until `state` is completed, failed or interrupted. A 202 is acceptance, not successful preflight, launch, guest correctness or test completion.

```http
GET /api/v1/jobs/build-149-a/operation
GET /api/v1/jobs/build-149-a/validation
```

Validation checks every declared file's committed upload/size/digest, ensures referenced package inputs are declared, and runs the existing package preflight (including target OS and Linux executable permission). Submission performs validation itself, then atomically moves the complete package into Pending. The normal runner still performs its launch/control/tool checks. API preflight success does not guarantee the environment or xemu will work.

Operation failures retain a machine-readable errorCode, explanation, recovery hint and available progress/report. A disconnected client can resume polling. A runner/process restart marks unfinished work interrupted, except a submitted package already in the queue is reconciled as published rather than submitted a second time.

Hashing/copying is done outside fast status polling and is tracked as activity for the whole operation lifetime. If a benchmark is already active, its operation policy may reject heavy operations with 409. If an already-running transfer/operation overlaps a later test, the existing activity tracker records the intervention; this is not a guarantee of zero benchmark impact.

## Follow the attempt and collect results

```http
GET /api/v1/jobs/build-149-a
```

Job states describe package ownership: draft, busy, queued, testing, tested, cancelled or unavailable. `tested` is a terminal queue disposition, **not** a correctness verdict. During testing, a run ID and run/result/log actions become available. Follow those links rather than looking for files through SSH.

```http
GET /api/v1/runs/<run-id>
GET /api/v1/runs/<run-id>/tail?file=stdout.log&bytes=32768
GET /api/v1/runs/<run-id>/artifacts/result.json
GET /api/v1/runs/<run-id>/artifacts/metrics.csv
```

The existing result response includes its assessment and artifact listing. Read execution/correctness/evidence/comparison separately. Downloads use the existing streaming/range implementation. Active benchmark policy can block tails/downloads; wait or explicitly change the plan for a future attempt, not bypass the policy with a shell.

`collect` downloads the **listed** artifacts of a tested job under `<output>/<run-id>/`, using .part files and byte ranges for interrupted local downloads. It checks byte counts, not an independent result digest (the existing artifact API does not supply a digest manifest). Existing server artifact enumeration limits still apply; collection is not a new unlimited archive endpoint.

For frequent activity polling use `/api/v1/status` and `/api/v1/metrics/latest`. They read cached telemetry. Do not substitute repeated validation, directory listings or discovery calls at 100 ms.

## Modify queued work and retry without touching evidence

Withdraw an unclaimed queued job:

```http
POST /api/v1/jobs/build-149-a/withdraw
```

It returns to draft, where the plan and payload uploads can be changed within the original file declarations. The queue and withdrawal compete by atomic rename. If execution claimed it first, withdrawal returns a conflict; the API does not edit Testing.

Cancel a draft or unclaimed queued job:

```http
DELETE /api/v1/jobs/build-149-a
```

Cancellation retains the files in a cancelled state. It does not delete completed evidence or kill a running process. Active xemu pause/resume/input/quit remain available through their existing target-control routes where policy permits. This patch does not add a policy-bypassing active process kill or remote OS command endpoint.

Clone a stable draft, tested or cancelled package into a **new** draft ID:

```http
POST /api/v1/jobs/build-149-a/clone
Content-Type: application/json

{"newId":"build-149-b"}
```

The tester copies and verifies the declared payload locally, so an unchanged large workload does not have to cross the network again. Poll the destination operation. After it completes, edit the new draft and submit it. Clone preserves source file declarations and changes Job.Id; use a new creation manifest when replacing the executable with different content.

```sh
python scripts/runner_api.py --url http://192.168.1.42:9368 clone build-149-a build-149-b
python scripts/runner_api.py --url http://192.168.1.42:9368 edit build-149-b ./variant-b-job.json
python scripts/runner_api.py --url http://192.168.1.42:9368 request POST /api/v1/jobs/build-149-b/submit
python scripts/runner_api.py --url http://192.168.1.42:9368 wait build-149-b
```

An interrupted clone is reported, not silently treated as a complete package. Cancel it and clone to a fresh ID, or inspect/fill its remaining declared payload through the upload API before submission.

## Error handling for agents

| Code or status | Required response |
| --- | --- |
| job_busy / upload_in_progress | Poll the current operation or file offset; retry after ownership is released. |
| job_identity_conflict | Reuse an ID only for the identical creation request; use a fresh ID for a new attempt. |
| plan_revision_mismatch (412/428) | Fetch the job and retry with its current revision in If-Match. |
| job_not_editable / job_already_started | Do not change the running attempt; withdraw before claim or clone after completion. |
| upload_identity_mismatch / upload_offset_mismatch | Query committed upload status and use that ID/offset. |
| preflight_failed | Read the structured validation report and correct the draft. |
| operation_blocked | Respect benchmark policy. Wait or change the next draft's intended policy. |
| job_claim_race | Re-read the job state. A concurrent queue claim may have won. |
| unavailable | The API-owned directory could not be located. Do not assume it is safe to resubmit. |

API-created package directories and internal metadata are runner-owned. Existing manually staged packages are not automatically adopted by this API. Do not rename API package directories outside the API or manipulate its hidden metadata. Results and control remain accessible through the existing endpoints.

## Verification boundary

This change includes a standard-library client and the HTTP/store implementation. Existing CI covers compilation/regressions; the client's syntax and CLI help can be checked locally without xemu. Real Windows/Linux API-only launch, native controller/capture behavior, interrupted 10 GB+ LAN transfer, and process-restart reconciliation require qualification on the test rigs. No native capture, large-network throughput or telemetry-overhead result is implied by a successful build.
