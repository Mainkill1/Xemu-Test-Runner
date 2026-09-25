# Test operation

Operate the existing tester over HTTP. No SSH/remote process launch, queue-directory edits or policy bypass is part of normal testing. Operator bootstrap/upgrade/recovery is separate.

Use scripts/runner_tests.py with runner_transport.py, runner_test_results.py and runner_wait.py beside it on the build/agent machine. Set XEMU_RUNNER_URL to the tester's LAN HTTP origin, then run `runner_tests.py connect` from that same machine. Do not SSH to the tester just to run the client against 127.0.0.1; the client rejects that pattern when it detects an SSH session. Report direct-HTTP reachability failures instead of silently substituting remote shell execution.

`upload DIR --exe EXE --id ID --tests ...` uploads an application and selects saved tests only. Add --start to authorize execution, or call start with the returned request IDs later. `select APPLICATION --id PREFIX --tests ...` reuses an application and also defaults to no start. A changed input or intentional rerun needs a new identity; identical IDs recover interrupted requests.

## XISO categories and campaigns

Use `runner_xiso.py` with runner_transport.py from the same checkout. The operator registers a matched suite/template and prepared clean seed once. Normal agents call `suites`, `categories SUITE`, `tests SUITE --category shaders`, and `select APPLICATION --id CAMPAIGN --category shaders`. Use `--test STABLE_ID` for individual leaves; repeat category/test options to union selections. Omit --suite only when exactly one suite is installed.

Most fields are optional. Do not create a large JSON document or guess defaults: missing settings inherit the registered suite and the frozen `plan CAMPAIGN` shows effective values. The small request is `{id,application,categories}`. Selection never starts without --start; `start CAMPAIGN` is separate. A reference application freezes per-chunk ABBA scheduling on the tester, not in an agent script.

Categories are cpu, commands, shaders, textures, geometry, surfaces and scenarios. Future unmapped tests are visible under other. The reviewed shader-inclusive target has 160 leaves plus five structural groups, not six total leaves. Its six shader-lifecycle cases execute in separate processes. Required memory-pressure checkpoints are expanded together and disclosed in the plan.

Keep XISO/catalog artifact identity together. `targets` provides a candidate pin, not a known-good baseline or permission to substitute the latest source catalog for an older ISO. Registration reads the actual ISO. Do not rebuild an ISO per selection, use guest networking, or edit a live/shared HDD. The runner injects a resolved plan only into a prepared private disk, preserves exact guest results/receipts after confirmed exit, and deletes transient HDDs after evidence. See docs/XISO-CAMPAIGNS.md for one-time preparation and qualification boundaries.

`runner_xiso.py wait CAMPAIGN` follows indefinitely without agent timers. `status` gives compact progress and comparison-eligible counts; `attempts` returns paged canonical child outcomes. All failures and missing evidence stay visible. Do not count missing children or calculate metrics from downloaded CSV when the runner can report them.

## Waiting and results

`runner_tests.py wait TEST_REQUEST_ID` has no overall cutoff or timer/interval/follow option. It follows the same test, reconnects transient read failures and prints final/attention only. Add --updates for compact heartbeat lines or --job for an ordinary API job. Direct routes are GET /api/v1/test-runs/{id}/wait and /api/v1/jobs/{id}/wait without query parameters. Client interruption never authorizes cancellation or a rerun. Finished means terminal, not passed.

The upload receipt supplies the executable SHA-256. `result SHA` uses the explicitly pinned baseline; `compare --a SHA --b SHA` delegates calculation to the tester. Never select the first/latest/fastest baseline implicitly. `baseline SHA` explicitly changes the pin; baseline without an argument only reads it.

Results match executable bytes, procedure/fixed inputs, environment and metric contract. Do not mix workloads or omit failed repetitions. Execution, correctness, evidence and comparison remain separate. Missing/unindexed evidence is not a pass; resolve index-status errors before claiming complete coverage.

Cache state is an input. Use RuntimeState.Isolation, explicit shader-cache behavior and private HDD/EEPROM. `state RUN_ID` exposes paths, before/after hashes/counts and unresolved controls. Cold application caches do not prove cold driver/OS caches. Do not enable AllowUncontrolledDriverCache just to obtain a green comparison, delete global caches or start unrequested warmups. See docs/RUN-STATE.md.

Raw CSV and ZIP are explicit secondary downloads. `diagnostics RUN_ID` is a compact crash/bundle report; `--out FILE.zip` downloads and verifies its hash. Missing dumps do not make a crash pass. Let other requested tests proceed after confirmed exit and archival. See docs/CRASH-REPORTS.md.

HDD assets belong in the disk catalog, not every package. Use disk-list/disk-upload/disk-import; pin IDs/hashes in RuntimeState.DiskAssets. Default deleteAfterEvidence retains extracted evidence, not boot-only runtime HDDs. Snapshot carriers are copied intact; disk-only internal snapshot conversion is not a full xemu VM save state. See docs/DISK-ASSETS.md.

Honor benchmark transfer/control policy. Retain definition sources and raw evidence referenced by results. The lower-level runner_api.py run/submit/retry commands intentionally execute and are not upload substitutes. Configuration/results remain local; no external database or extra live sampler is needed.

Remaining capability gaps are in docs/AGENT-WORKAROUND-AUDIT.md. Report them instead of replacing tester behavior with untracked files, analyzer scripts, guest networking or shell manipulation.
