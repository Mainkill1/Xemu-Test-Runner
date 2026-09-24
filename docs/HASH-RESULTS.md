# Executable-hash results and known baselines

Executable identity is the complete SHA-256 of its bytes. Names, upload IDs and filenames do not choose a baseline or group builds. Each archived attempt still has its own run ID, procedure identity and captured environment; a hash does not erase differences between test conditions.

## Normal operation

```sh
python scripts/runner_tests.py baseline KNOWN_EXECUTABLE_SHA256
python scripts/runner_tests.py result CANDIDATE_EXECUTABLE_SHA256
python scripts/runner_tests.py compare --a REFERENCE_SHA256 --b CANDIDATE_SHA256
python scripts/runner_tests.py csv RUN_ID ./raw-metrics.csv
```

`baseline SHA` is an explicit write. `baseline` without a hash only displays the pin. The program never silently selects its first, newest or fastest build as known-good. The baseline snapshots all currently indexed attempts for the chosen executable; later runs of the same hash do not alter it. Changing the baseline requires another explicit command.

Result and comparison commands print a small Markdown report returned by the tester. Global `--json`, or `--format json`, returns compact structured data. `--format csv --out comparison.csv` exports a comparison table. `csv RUN_ID FILE` separately downloads the original sampler CSV. Comparison CSV is derived data and is not a replacement for raw metrics.csv.

The Python helper forwards hashes and prints the server response. It does not download measurements to calculate percentages, choose a baseline, hide failed runs or invent performance results.

## API

| Request | Behavior |
| --- | --- |
| GET /api/v1/build-results/SHA | Small result summary and default pinned-baseline comparison. |
| GET /api/v1/build-results/SHA/runs?offset=0&limit=20 | Explicit paged full indexed attempt records, with measurement values and raw CSV URLs. |
| GET /api/v1/compare?A=SHA&B=SHA | Server-side comparison of the two indexed executable result sets. |
| GET /api/v1/compare?B=SHA | Use the immutable local baseline snapshot as A. Missing baseline returns a conflict. |
| GET /api/v1/baseline | Read pin identity, revision, time and attempt count. |
| PUT /api/v1/baseline with {"sha256":"..."} | Explicitly replace the known reference snapshot. Ineligible/unmeasured/ambiguous indexed attempts prevent pinning. |
| POST /api/v1/build-results/index with {"runId":"..."} | Import existing canonical archived evidence; no execution. |
| GET /api/v1/build-results/index-status | Inspect recent automatic indexing problems. |

Append `format=markdown` for readable reports. Comparison also supports `format=csv`. JSON summaries are byte-bounded and retain MoreRows/MoreRuns/MoreMetrics indicators when details are omitted. Full CSV and paged run records are secondary detail operations. API error/absence is never a passing result.

## What is compared

A cohort has the same normalized executed JobDefinition and fixed-input hashes, plus matching captured host/environment, runner version, monitoring interval and GPU-provider configuration. Attempt IDs, test display names, experiment labels and executable filename/digest slots are excluded from workload identity. Fixed workload/config/seed content remains included. The executed job file must match the input manifest's saved job-file hash.

Each metric additionally requires the same name, unit and direction. The server takes a median of per-attempt values and calculates `(B / A - 1) * 100`. Lower-is-better and higher-is-better determine the displayed direction. A zero reference has no invented percentage. No raw sample series is pooled across runs.

Failed or incomplete repetitions block their cohort's percentage rather than disappearing from the average. Missing workloads, different environments, missing metrics and ambiguous dependency bundles remain explicit, incomparable rows. Having the same executable hash with different build dependencies is not permission to merge incompatible repetitions.

These are descriptive comparisons, not statistical-significance tests or proof that every possible source of variation was captured. Correctness/evidence/comparison outcomes remain separate. A metric is eligible only with completed execution, passed correctness, complete evidence, eligible canonical assessment, and verified API payload provenance.

## Persistence and indexing

Normalized immutable records live under `Results/.build-results/EXE_SHA/RUN_ID.json`. The local baseline is `Results/.build-results/baseline.json`, containing a frozen set of those normalized records and a content revision. Restarting the program does not choose a different baseline. Retain raw Results/run directories for the linked original CSV/artifacts; keeping the normalized baseline alone does not retain all raw files.

Automatic indexing runs only at idle boundaries and handles finalized archived API-owned jobs, including explicitly requested tests. It reads canonical result.json, assessment.json, input-manifest.json, host-inventory.json, the archived job configuration and its API validation/manifest. It does not scan or parse raw CSV. Existing archived runs can also be indexed explicitly. Manually staged/non-API jobs are not silently adopted.

Malformed/missing provenance is rejected rather than defaulted to success. An indexing failure is not a test pass; consult index-status and the original attempt. Reports describe **indexed archived attempts**, not a guarantee that every historical folder was indexed. Complete the required test set and resolve indexing failures before selecting a known baseline or claiming coverage. RecordSet fingerprints identify the precise compared snapshots.

Indexing, baseline writes and hash-result analysis respect existing bulk-transfer/benchmark policy. They are explicit analysis work, not the lightweight active-job status path. No test process is launched by indexing, reading, comparing or pinning.

## Validation scope

HTTP fixtures cover renamed executables, median arithmetic, default pins, non-drifting baseline snapshots, failed repetitions, context mismatch, zero references, archived ownership, malformed assessment rejection, output bounds and separate raw CSV. Client fixtures verify that A/B values are forwarded to the tester and result inspection performs no raw download. Synthetic fixture measurements are not real xemu performance evidence.
