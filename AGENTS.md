# Test operation

Operate the existing tester over HTTP. No SSH/remote process launch, queue-directory edits or policy bypass is part of normal testing. Operator bootstrap/upgrade/recovery is separate.

Use scripts/runner_tests.py with runner_transport.py and runner_test_results.py beside it on the build/agent machine. Set XEMU_RUNNER_URL. First list and inspect saved configs. Config upload saves a named immutable revision; it never launches a test.

`upload DIR --exe EXE --id ID --tests ...` uploads one application and stores selected test requests only. Add --start to explicitly authorize execution, or call start with the returned request IDs later. `select APPLICATION --id PREFIX --tests ...` reuses an existing application and also defaults to no start. Active tests are not interrupted; requested work queues and waits for preparation/execution. Do not turn an upload request into a submit/run command.

Use complete test revisions when a name is ambiguous. Reuse identical IDs/inputs after a lost response; a changed application/test or intentional rerun needs a new identity. Multi-test starts are individual durable requests, not an atomic transaction.

The upload receipt provides the executable SHA-256. `result SHA` returns the tester's compact report against its explicit pinned baseline. `compare --a SHA --b SHA` delegates all comparison arithmetic to the tester; omit A for the pinned default. Never select a first/latest/fastest baseline implicitly. `baseline SHA` is an explicit pin write; baseline with no argument only reads.

Results are grouped by executable content, actual test procedure/fixed inputs, environment and metric contract. A hash is not permission to mix workloads or ignore failed repetitions. Inspect execution/correctness/evidence/comparison separately; tested/API success is not guest correctness. Missing or unindexed evidence is not a pass. Resolve index-status errors before asserting complete test coverage.

Use `csv RUN_ID OUTPUT` only for explicit raw data needs. Routine result/compare does not download raw CSV or process metrics on the agent. Lower-level runner_api.py provides detailed draft/control/evidence workflows and explicit correctness/eligibility exit gates. Its run/submit/retry commands intentionally execute, unlike upload/select.

Honor benchmark transfer/control policy. Already-staged work can be queued without bulk transfer. Keep saved definition source packages and the original run evidence needed by raw artifact links. Configuration and result stores are local to the tester; no external database is required.

For crashes, use `runner_tests.py diagnostics RUN_ID` for one compact report. Add `--out FILE.zip` only for an explicit diagnostic download; the client verifies the ZIP SHA-256. The same ZIP is retained beside the archived executable. Missing dumps or failed collection do not make a crashed attempt pass and do not authorize a rerun. Let other already-requested tests proceed after the target is confirmed stopped. See docs/CRASH-REPORTS.md for provider prerequisites and bounds.

Known remaining workflow gaps are recorded in docs/AGENT-WORKAROUND-AUDIT.md. Report missing batch/cancellation/retention capabilities rather than silently replacing them with queue-directory or shell manipulation.
