# Remaining agent-workaround gaps after crash ZIP support

This is a source-based follow-up backlog, not a claim that these capabilities are already implemented. Audit scope: the #20 stack plus #24 crash reporting. The working source paths below are the evidence for each gap.

## 1. One durable batch and complete-result accounting

`runner_tests.py:select_tests` creates independent requests and then starts them individually. A partial network failure can leave only some starts accepted. `BuildResultStore` compares indexed archived attempts, not the exact list originally requested by the agent. There is no batch identifier with a definitive requested/running/passed/crashed/cancelled/missing ledger.

Likely workaround: agents retain JSON manifests, poll every ID, count missing repetitions and combine results themselves.

Smallest useful follow-up: immutable batch request listing test revisions/repeats/build hash, explicit batch start, per-child receipts, and one compact batch outcome with missing/unindexed children. A crash remains a completed failed child; it never aborts unrelated authorized children. Hash comparison should declare coverage, not just the successful indexed subset.

## 2. Typed cancellation and ownership-aware recovery

`AgentJobStore.Cancel` cancels only draft/unclaimed packages. The target-control quit path is a separate control action governed by pause/benchmark policy. `RunnerEngine` can hold Testing for uncertain ownership or exhausted archival attempts; there is no small recovery API that distinguishes safe re-archive from unsafe overlap.

Likely workaround: agents kill processes, restart the runner or manipulate Testing/lock files through a shell.

Follow-up: explicit per-run cancel with recorded intent, bounded termination, retained evidence and cancelled outcome; separate pause-admission/reorder operations; inspect/reconcile/re-archive recovery actions that verify actual process identity before releasing ownership. Do not make a failed process-state query equivalent to permission to run another target.

## 3. Retention and storage maintenance

The test library retains a source package reference, not an independent content store. Build baselines persist normalized results while raw links still require the run directories. No reference-aware cleanup API resolves live drafts, baked-test sources, pinned baselines, symbols and diagnostic ZIPs together.

Likely workaround: SSH deletion to reclaim disk, or repeated asset uploads after a source package is removed.

Follow-up: storage inventory, safe cleanup preview, protected references, explicit prune with receipts, and configurable run/dump retention. This is especially important now that dumps and ZIP mirrors add storage. Do not remove the only baseline evidence or assets required by a saved test.

## 4. Complete crash readiness and symbol identity

The new crash-capabilities route reports configured limits/tool presence. It deliberately does not claim WER registry/permissions, systemd collection policy or PDB/DWARF identity are fully qualified. ReportFiles are explicit package-relative paths; alternative OS crash handlers and arbitrary shared report locations are not covered automatically.

Likely workaround: agents install tools or copy symbol/report files and run a debugger remotely.

Follow-up: per-executable-hash symbol upload/validation, clear provider readiness tests, an explicit operator bootstrap check, and bounded postmortem re-analysis of retained dumps without rerunning tests. Match PE/PDB or ELF build identity; a filename alone is insufficient. Keep initial OS administration distinct from normal test operation.

## 5. One packaged client and complete discoverability

Two script entry points currently remain: the focused runner_tests.py and lower-level runner_api.py, with adjacent Python modules. publish.sh/publish.ps1 publish the C# project and tools content, not a versioned agent-client package. OpenAPI primarily describes the older package workflow; later features rely on focused help and documents.

Likely workaround: agents copy an incomplete script set, use stale clients, or invent curl/SSH workflows because discovery does not show the capability they need.

Follow-up: one versioned standard-library zipapp/client download with a compatibility handshake, a single short onboarding path, and machine-readable schemas/examples for all supported operations. Keep raw/debug commands explicit rather than making normal testing a large multi-tool interface.

## 6. Ready-to-use measurement contracts

Hash comparisons use declared per-attempt measurement values; the app does not infer arbitrary FPS/frame-time metrics from unknown CSV schemas. Missing workload measurements are explicit and raw CSV remains available.

Likely workaround: agents download CSV and calculate frame percentiles, throughput or regressions locally.

Follow-up: a small set of tested xemu metric presets and server-side summaries for known telemetry schemas, with units, direction, sample coverage and repetition counts. Avoid arbitrary agent-written analysis scripts as the normal path.

## Deployment boundary

A stopped runner cannot answer its own start request. Installation, service supervision, upgrades and failed-machine recovery remain operator concerns until a separate trusted supervisor exists. These are not reasons to permit routine queue/result operations via SSH. Deploy the matching reviewed server/client stack before interpreting a capability_missing response as agent noncompliance.

Priority: batch completeness and cancellation/recovery first, then storage/retention and symbol readiness. The goal is to remove necessary agent bookkeeping and shell intervention, not to prohibit tools while leaving the required capability absent.
