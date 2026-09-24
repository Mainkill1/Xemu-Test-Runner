# Built-in performance reports

Run the client on the agent/build machine, not through SSH on the tester. For example:

```sh
python scripts/runner_tests.py --url http://10.0.7.1:9368 connect
python scripts/runner_tests.py --url http://10.0.0.115:9368 connect
```

These are direct read-only checks against the two example LAN addresses. They prove only connectivity from the machine running the command. `127.0.0.1` refers to that same machine; it is not the remote tester unless the client is actually running there. A network failure requires resolving routing, the configured listener bind, or an authorized firewall rule. The helper never substitutes SSH or changes network settings.

## Declare analysis once in the saved test

```json
{
  "Workload": {
    "Analysis": {
      "Segment": "in-game-throttle",
      "MetricsPath": "metrics.csv",
      "GuestFlipsPath": "guest-flips.log",
      "GuestFramesPath": "guest-frames.log",
      "FlipTailSamples": 6,
      "FrameTailSeconds": 30,
      "MinimumCpuSamples": 1,
      "MinimumFrameSamples": 1
    }
  }
}
```

Merge this section into the real test; keep its existing correctness checks, state isolation and measurement segments. Guest source paths are optional for CPU-only tests. The paths/windows/sample requirements are part of the immutable saved revision. The example minimums show syntax, not recommended sufficiency for a performance claim: select the coverage needed for the workload before execution.

The existing runner collects the artifacts. During post-exit workload evaluation, the built-in analyzer reads each selected source once, computes a report, hashes the source bytes, and saves `performance.json`. It adds measurements to the existing canonical result and hash-comparison pipeline. No new live sampler, helper process, service, agent code or per-poll CSV parsing is involved.

```sh
python scripts/runner_tests.py performance RUN_ID
python scripts/runner_tests.py --json performance RUN_ID
python scripts/runner_tests.py compare --a REFERENCE_SHA --b CANDIDATE_SHA
```

`GET /api/v1/runs/RUN_ID/performance` returns the stored report with canonical execution/correctness/evidence/comparison, executable identity and runtime-HDD cleanup. `?format=markdown` returns a small two-decimal table. Reading never reruns analysis, mutates a test, starts work or downloads raw evidence. Missing legacy reports return `analysis_not_recorded`, not zero measurements or a passing result.

## Calculations and scope

- CPU: exact segment match in metrics.csv, finite nonnegative core-percent samples; mean, median, count, min/max and p95/p99 in the detailed report. Missing CPU/duty cells are counted separately.
- Collector: duty distribution and overrun count for that same segment. A missing or malformed overrun field is not treated as zero.
- Guest cadence: the last declared number of `elapsed_us=... frames=... fps=...` records; total frames divided by total elapsed time. Rounded printed FPS values are never averaged.
- Frame intervals: positive deltas for timestamps in the final declared window, inclusive of its start. Mean and p50/p95/p99 use sorted per-frame deltas and linear interpolation at `(n-1)*p`. Timestamp/counter regressions are errors. The report includes actual first/last included timestamps, count and excluded zero intervals.

Guest timestamps are their own clock domain. The final guest window is not automatically claimed to coincide with a host metrics segment; record an aligned workload procedure or use explicit measurement markers. These statistics are descriptive. Counts/exposure, correctness, cache state and environment still determine whether comparisons are defensible.

A/B reports already expose per-attempt means, medians, ranges and sample counts. The new measurements participate as separate named metrics, without pooling host samples across attempts or across Windows/Deck. Ineligible runs remain in the compared cohort; performance analysis does not turn failed gameplay checks or unmanaged state into eligibility.

## Bounds and errors

Each source is limited to 64 MiB and one million records. CSV rows/lines are bounded to 64 KiB, CSV width to 128 columns, and retained metric/frame samples to 250,000. Inputs are run-relative, reject traversal/links, and must remain stable while read. Limits are errors rather than silently truncated successful analyses. Missing sources, nonfinite samples, malformed timing, zero elapsed cadence records and inadequate sample counts produce evidence failures. Other independent source results can remain available, but the failed contract is visible.

Raw files and numeric precision remain unchanged. The JSON report retains source SHA-256 values and a profile hash. The human report alone rounds to two decimals. This is not a retroactive repair of previously ineligible results, an automatic baseline promotion, or a statistical-significance test.
