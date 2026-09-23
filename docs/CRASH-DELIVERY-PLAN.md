# Crash diagnostics delivery

Approved task: distinguish native crashes from test failures, timeouts, cancellations and runner failures; preserve reports/dumps in a bounded ZIP beside the finished executable; continue already-requested tests after confirmed target termination. Missing diagnostics never rewrite the crash or hold the queue. No diagnostic rerun is authorized automatically.

Implementation sequence:
1. Reproduce native crash → archive → next-test behavior through the real engine on Windows/Linux. Preserve exact qualification source and diagnostic evidence in CI.
2. Add a small crash result contract and passive OS-specific post-crash collectors with explicit readiness/errors. Preserve native Linux wait status without guessing from 128+signal exit codes. No debugger in normal benchmark execution.
3. Package completed result/config/identity/logs/diagnostic artifacts into diagnostics.zip with per-file hashes, bounds and omission reasons; mirror beside the archived executable. Failed or unavailable ZIP/dump capture remains a separate outcome.
4. Expose compact crash summaries and ranged ZIP retrieval through existing APIs/client; retain crashed/unmeasured attempts in build comparisons.
5. Run all existing and new checks and record remaining agent-workaround gaps. Native dump providers/symbolization need real-rig qualification when absent from hosted CI.

Physical machine/storage failure or an unkillable process is not solved by falsely claiming termination and launching overlapping benchmarks. Such ownership failures remain explicit. Ordinary crashes and confirmed-terminated timeouts must not create those holds.
