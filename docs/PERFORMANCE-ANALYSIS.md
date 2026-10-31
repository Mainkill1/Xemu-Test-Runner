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

## Opt-in native-clock alignment

For a **new immutable saved-test revision**, explicitly select a feasible window
and group count as well as the timing mode, for example:

```json
{
  "TimingMode": "qemu-realtime-segment-v1",
  "FrameTailSeconds": 30,
  "FlipTailSamples": 4
}
```

Merge these fields into the existing `Workload.Analysis` declaration. Leaving the timing-mode
field absent preserves the legacy descriptive algorithm, schema1 report and
profile identity. Aligned reports use schema2 and include the chosen native
clock, fixed interval, event exposure, source bounds and end gap. Do not edit
archived results or silently reinterpret a legacy report as aligned evidence.

This contract is for the known xemu NV2A frame/flip logger, with exact executable
identity retained in the normal input manifest. It is not a generic clock-offset
guess for arbitrary logs. Measurement markers retain native absolute
`HostTimestampUs` and `ClockDomain`: Linux64 `CLOCK_MONOTONIC`, or Windows QPC
scaled to microseconds with integer arithmetic. The recorder calls the native
APIs; it does not assume the epoch of .NET `Stopwatch`. Unsupported/unavailable
clocks leave these fields absent and cannot qualify aligned evidence. A QEMU
build using a different realtime-clock implementation is outside this contract.

The selected segment must have exactly one ordered start/end pair with one
supported clock domain. The fixed frame window ends at that segment's end and
starts `FrameTailSeconds` earlier; the entire interval must fit inside the
segment. Frame records must bracket both boundaries, including a record **after**
the end. Declare a fixed collection/drain step after `segment_end` and before
quit in the new saved procedure, for example a one-second wait appropriate for
the expected workload. This is outside the measurement interval and must be
identical across compared builds. Missing coverage is incomplete evidence, not
permission to shift the window, change a wait, replace an attempt or retry until
the result becomes favorable.

The producer contract additionally requires a complete frame sequence beginning
at counter1/delta0, contiguous counters and deltas matching timestamp differences.
Both NV2A loggers begin on the same call. Each accumulated flip count and elapsed
time must match an actual frame record relative to that first timestamp. Every
expected producer flush, including the trailing groups, must be present: the
logger emits at the first frame at least five seconds after the preceding
boundary. Truncating a suffix cannot make an older group become the final group. This
validates the common origin of duration-only flip records rather than inferring
one from elapsed totals alone. The last `FlipTailSamples` complete groups wholly
inside the fixed frame window determine weighted flip cadence; their actual
start/end coordinates are disclosed. Too few complete groups, inconsistent
origins or truncated sources cannot produce aligned guest metrics.

The legacy defaults of six groups and a 30-second tail are unsuitable for this
aligned contract: groups usually exceed five seconds and boundaries need not
coincide with the host window. With a flip source, configuration validation
requires `FrameTailSeconds > (FlipTailSamples + 1) * 5` for nominal phase margin.
This does not guarantee coverage under slow or stalled progress; runtime checks
still require the actual complete groups. Frame-only profiles do not require a
flip-group margin.

Positive frame intervals ending in `(window start, window end]` provide the
latency distribution. `guest/window_frame_events` counts all events in that same
interval, including zero intervals; `guest/window_elapsed_s` retains the fixed
exposure, and `guest/window_cadence_fps` is their ratio. These are NV2A guest frame
events, not host-presented FPS or proof of identical simulation progress.
`guest/window_end_gap_ms` is the time from the last event to the fixed end: it is
a **right-censored gap**, not a completed frame latency. Inspect this gap together
with event cadence and completed-interval tails; an interrupted final frame must
not disappear behind a favorable percentile. Full source records remain intact.

Before: final log tails can describe boot while CPU samples describe a later
game segment. After: native markers select a fixed interval, source/clock/origin
checks validate coverage, and incomplete data retains an explicit failed
evidence check. Correctness and canonical eligibility remain separate. A valid
aligned report still does not prove rendering correctness, statistical
significance or a game speedup.

A/B reports already expose per-attempt means, medians, ranges and sample counts. The new measurements participate as separate named metrics, without pooling host samples across attempts or across Windows/Deck. Ineligible runs remain in the compared cohort; performance analysis does not turn failed gameplay checks or unmanaged state into eligibility.

## Bounds and errors

Each source is limited to 64 MiB and one million records. CSV rows/lines are bounded to 64 KiB, CSV width to 128 columns, and retained metric/frame samples to 250,000. Inputs are run-relative, reject traversal/links, and must remain stable while read. Limits are errors rather than silently truncated successful analyses. Missing sources, nonfinite samples, malformed timing, zero elapsed cadence records and inadequate sample counts produce evidence failures. Other independent source results can remain available, but the failed contract is visible.

Aligned mode also bounds retained flip groups to 250,000 and hashes
`segments.jsonl` as an analysis source. It scans each source once, stores only
flip boundaries and selected frame samples, and never adds a live sampler.

Raw files and numeric precision remain unchanged. The JSON report retains source SHA-256 values and a profile hash. The human report alone rounds to two decimals. This is not a retroactive repair of previously ineligible results, an automatic baseline promotion, or a statistical-significance test.
