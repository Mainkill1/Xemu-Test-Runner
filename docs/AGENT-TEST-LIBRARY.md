# Bake once, run by reference

A reusable test is a content-addressed snapshot of a complete JobDefinition, its declared payload hashes, its source package, and a small allowlist of replaceable build files. It is not a second execution engine and it is not a test-pass certification.

## Register a test once

Upload a complete source draft using the existing job/upload API. It can also be an archived tested or cancelled API-owned package. Then:

```http
POST /api/v1/tests/smoke/bake
Content-Type: application/json

{"sourceJobId":"seed-job","description":"Pinned smoke workload"}
```

The response returns `id`, `revision`, counts and a detail URL, never the expanded plan. Revision is the SHA-256 identity of the stored definition. Repeating an identical bake retains the same revision; changed definitions create new revisions without replacing old ones.

Default `buildFiles` contains only the package's executable. For builds with replaceable DLLs/assets, explicitly declare their package-relative paths in the bake request. Paths must already be in the source manifest. Runtime-state seeds cannot be replacement slots. Adding dependencies or changing fixed workload/config/assertions requires a new baked definition.

`GET /api/v1/tests?limit=10&offset=0` lists compact definitions and `nextOffset`. `GET /api/v1/tests/{id}/{revision}` returns the summary and build slot paths. `?view=definition` opts into the full stored definition; it is subject to bulk-transfer policy. Discovery advertises `pinnedTests` and `payloadReuse`; focused help is at `/api/v1/help?topic=tests`.

## Create an attempt without sending its plan

```http
POST /api/v1/jobs/from-test
Content-Type: application/json

{"id":"pr149-a","testId":"smoke","revision":"<64-hex revision>"}
```

The reference request expands the frozen plan on the tester and creates a new draft. The response is a small 202 receipt with an operation URL. Poll until the copy operation is complete, then submit the draft through the existing `/api/v1/jobs/{id}/submit` endpoint. Preparation is not submission, launch or a correctness verdict.

For a new executable, include only its changed declaration:

```json
{
  "id":"pr149-b",
  "testId":"smoke",
  "revision":"<64-hex revision>",
  "files":[{"path":"xemu.exe","length":123456,"sha256":"<actual binary SHA-256>","executable":true}]
}
```

Unchanged files are copied and hash-verified on the tester through the same FileUploadStore. Changed files remain missing until uploaded to the normal draft file URLs. Query upload status, send only the missing bytes, and submit. The executable's expected digest and declared build-input digests are updated to the requested build; workload, correctness, runtime-state and operation contracts remain unchanged.

Optional `experimentId`, `variant` and `reference` labels allow baseline/candidate grouping without resending the experiment contract. Labels are bounded; the normal plan validator checks their consistency. Arbitrary Plan/Operations overrides are rejected rather than silently ignored. For an intentional contract change, edit a separate draft and bake a new revision.

The expanded frozen job includes `test-definition:<id>@<revision>` in Tags. Existing retained job evidence and job/input hashing therefore preserve which definition was used. No live plan is edited and no result evidence is rewritten.

## Reuse outside the library

A normal new draft with a new full manifest can also request:

```http
POST /api/v1/jobs/new-build/reuse
Content-Type: application/json

{"sourceJobId":"retained-build"}
```

Only files matching path, length and SHA-256 are copied. The operation reports reused/remaining file counts. This supports new-build manifests without a global artifact cache or another network upload of unchanged inputs.

## Identity, ownership and limits

Reuse the same request and job ID after a lost response. A different expanded request with that ID conflicts. Once submitted, an identical retry observes the existing attempt rather than copying or launching it again. An intentional repeat needs a new ID.

Source and destination are reserved for the copy. Queued/running sources are rejected; no hard links or shared writable state are introduced. The existing runtime preparation still creates private state for execution. Copies verify actual content; missing/corrupt source data fails visibly. Interrupted preparation retains its draft and operation receipt; retry/repair that draft, not a duplicate attempt.

This first library retains a reference to its source package for payload reuse. Keep that source package. It is not an independent retention system; removing a source can make future preparation fail, while already materialized attempts remain independent.

Benchmark policy applies before mutation, and hashing/copying is journaled by the existing operation activity tracker. This retains existing overlap accounting, not a new scheduler-level guarantee of zero transfer overlap.

Definitions retain the existing 1 MiB JobDefinition and 4,096-file limits. Catalog lists are paged; build replacement slots are capped at 128. Baking checks upload completion and plan structure; reuse and submission verify payload hashes. No built-in game assets, legal rights, firmware, native correctness results or GPU qualification are implied.

## Verification

`dotnet run --project tests/AgentChecks -c Release` covers a 1,000-step definition invoked by a sub-256-byte request, content revisions, fixed-input protection, changed-binary reuse/upload, source preservation, required pins, idempotent retry and benchmark refusal. CI runs these HTTP fixtures on Windows/Linux alongside the existing regression/process checks.
