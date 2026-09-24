# Compact agent observations

Use `GET /api/v1/agent?view=summary` once to verify capabilities. Detailed discovery and existing endpoint responses remain unchanged. Focused help is at `GET /api/v1/help?topic=observations`.

## Observe one job

`GET /api/v1/jobs/{id}?view=summary` returns identity, state, run ID, a lifecycle cursor, a blocker, brief operation progress and one safe next read. It does not return the job plan, payload manifest or telemetry inventory.

For a bounded wait, send `since=<cursor>&wait=20`. The maximum is 20 seconds, with 32 concurrent long waits per server. An unchanged timeout returns HTTP 200 with `changed:false`. Reuse the same ID; waiting never creates, cancels or repeats an attempt. A server restart changes the cursor epoch. Plan revisions remain separate and retain their existing If-Match semantics.

A held attempt is reported as `held`, not ordinary progress. Queue blockage and missing ownership are explicit. Follow the blocker rather than deleting locks or staging a duplicate.

Observation polling reads queue location and the small attempt journal. Operation progress is cached by receipt file version; compatibility receipts are deserialized without their full result objects only when changed. It does not call the full job view, hash payloads, enumerate artifacts or sample hardware. Recommended client polling is one bounded wait at a time, not a 100 ms loop.

## Read an outcome

`GET /api/v1/runs/{runId}?view=summary` projects the runner's existing `RunAssessment`: execution, correctness, evidence and comparison remain four independent outcomes. `ok` means the HTTP operation succeeded. It does not mean the test passed.

Missing, unreadable, oversized, incomplete-schema or malformed assessment files return `available:false`, an explicit code, and `outcome:null`. They never default to pass. The detail link targets the existing assessment artifact, subject to the existing bulk-transfer policy.

Default results contain at most four comparison reasons and four failed checks. Text and serialized output are bounded; `moreReasons`, `moreFailures` and `truncated` make omissions visible. The reader caps assessment input at 1 MiB and caches at most 128 file versions. It reads no raw metrics, logs, screenshots or diagnostic captures. Small observations remain available during benchmarks; raw artifact transfers remain policy-controlled.

## Verification

`dotnet run --project tests/AgentChecks -c Release` exercises the actual HTTP listener with temporary fixture packages, alongside the existing regression/process checks on Windows and Linux CI. Fixtures enforce 2 KiB discovery, 1 KiB ordinary job observations and 4 KiB result summaries, including a 1,000-step plan and oversized reason strings.

These are contract/fixture checks, not native xemu/GPU or large-network qualification. The existing result-detail and OpenAPI package-workflow contracts are not replaced by these additive views.
