# Inspect, upload, request

The small test-focused client is `scripts/runner_tests.py` (Python 3.10+, with runner_transport.py beside it). Set XEMU_RUNNER_URL once. It operates the existing HTTP runner, not a second remote shell or executor.

```sh
python scripts/runner_tests.py list
python scripts/runner_tests.py show smoke --out smoke.json
python scripts/runner_tests.py config-upload vulkan-smoke ./edited.json --assets seed-smoke
python scripts/runner_tests.py upload ./candidate --exe xemu.exe --id build-149 --tests smoke vulkan-smoke
python scripts/runner_tests.py start build-149-t001 build-149-t002
```

The upload command alone never runs tests. To upload and explicitly request the selected tests in one invocation, add `--start`. `select APPLICATION --id PREFIX --tests ...` selects more tests against a retained application without another binary upload; it also defaults to no start.

The application uploads once. Requested tests reuse its executable/dependencies locally, copying the executable into the path required by each config even if the uploaded filename differs. Fixed workloads/firmware/config/seeds come from the test definition's retained source. All payload copies and normal preflight verify declared hashes before publication to the existing JobQueue.

A bare test name is accepted when it resolves to exactly one revision. Multiple revisions require `NAME@FULL_SHA256`; the helper resolves/pins names before changing state. Each request gets a stable ID. Repeating the same upload/request/start is idempotent; changing an existing request's application/test conflicts rather than silently replacing it.

## Config transparency

Open `/tests` for a plain browser list, full JSON viewer/download, named config upload form and explicit Start buttons for uploaded selections. `/api/v1/test-configs` is a paged catalog (limit/offset). POST `/api/v1/test-configs/NAME` accepts `{sourceJobId,job,description?,buildFiles?}`. GET `/api/v1/test-configs/NAME/REVISION` exposes the complete frozen definition. Config upload validates the JobDefinition and asset references without editing the source plan or starting it. Source asset bytes may be uploaded separately; readiness and hashes are checked before execution.

## Explicit queue

POST `/api/v1/test-runs` with `{id,applicationJobId,testId,revision}` stores an unstarted selection. GET `/api/v1/test-runs/ID` shows state. POST `/api/v1/test-runs/ID/start` with no body or `{}` persists execution intent and returns 202. DELETE cancels an unprepared request only.

State flow: uploaded → queued → preparing → queuedForExecution → running → tested. Missing uploads remain waitingForUpload. Preparation failures and held ownership are visible; they are not passed tests. `tested` means archived, not correct.

Explicit start is accepted during an active benchmark. Preparation/copy/validation is deferred until the runner reports idle and no package remains in Pending/Testing. Requests are dispatched in start-request order, one at a time, behind existing work. The dispatcher never launches processes. On restart it resumes only persisted start requests, reconciles already-published packages, and never turns upload-only selections into executions.

Actual new binary/config uploads still obey the existing active-benchmark bulk-transfer policy. Stage applications before a benchmark, or complete the upload when transfers are permitted; enqueueing already-uploaded work does not need bulk transfer. This preserves the earlier benchmark isolation policy rather than quietly contaminating measurements.

The dispatcher is supervised with the HTTP listener and stops on the same lifetime token. Existing ordinary API submissions can still race admission; existing activity accounting applies to any overlap. This is not a new scheduler-level zero-interference claim.

The previous `runner_api.py run`, `submit`, `retry` and `submit-draft` commands remain explicit execution requests. The new upload/select commands do not call them. Configuration files and selected requests remain inspectable before start.

## Checks

`dotnet run --project tests/AgentChecks -c Release` covers config inspection/new names/source preservation, upload-only behavior, explicit start during benchmark, local reuse despite executable renaming, serial dispatch, idempotency, cancellation and invalid-request handling. CI is fixture verification, not native xemu/GPU qualification.
