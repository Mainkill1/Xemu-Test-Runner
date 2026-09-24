# Built-in performance analysis

The runner, not an agent-created script, must calculate the segment and frame statistics requested in PR184's test: CPU mean/median/count, collector duty/overruns, weighted guest flip cadence, final-window frame intervals with mean/p50/p95/p99/count, executable identity, canonical outcomes and HDD cleanup.

Implement this as an optional versioned `Workload.Analysis` profile in the saved immutable test. No new sampler, process, service or live poll is needed. Workload finalization analyzes the existing artifacts once after confirmed target exit, then persists `performance.json` and adds declared measurements to the existing comparison pipeline. GET reads only the small saved report and existing assessment/identity/cleanup receipts. Original inputs and precision remain unchanged. Missing data never implies zero or success.

The profile pins the segment, source paths, tail windows and minimum sample requirements before execution. Percentiles use linear interpolation at `(n-1)*p`. Cadence is total frames / total elapsed time, not the mean of rounded FPS strings. A/B distributions remain grouped by test/input/environment contracts, and include all repetitions and ineligible outcomes rather than cherry-picking good rows. Different host results are not pooled.

Implementation steps: add failing raw-artifact and HTTP tests; implement bounded streaming post-run analysis and provenance; connect canonical workload evaluation and read APIs; expose a thin client report command and direct-LAN connection check; verify Windows/Linux regression and byte-preservation checks. Legacy runs without the profile remain explicitly uncomputed and are not retroactively qualified.
