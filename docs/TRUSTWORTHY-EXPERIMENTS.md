# Trustworthy experiment contracts

The runner is an execution and evidence appliance. It should not turn process success into an engineering conclusion automatically.

## Four independent outcomes

Every attempt should be interpreted through:

1. **Execution** — runner/process/control outcome.
2. **Correctness** — declared workload assertions.
3. **Evidence** — required artifact completeness.
4. **Comparison** — whether this attempt is eligible for its experiment.

A run can be execution-complete while correctness fails. That is useful evidence of a regression, not a broken runner.

## Package contract

A package contains the candidate executable, job.json, configuration, declared inputs, optional runtime-state seeds, diagnostic recipes, and test assets.

The queue stability signature includes:
- job.json
- executable
- RequiredFiles
- Inputs
- RuntimeState seed files

An incomplete package should normally be staged under a dot-prefixed directory and atomically renamed into Pending. The runner also scans all visible pending packages; a broken alphabetically-first package does not block a later independent ready package.

## Runtime isolation

Use RuntimeState for writable HDD/EEPROM/cache/test-state files. Each attempt gets a private directory under workspace/Runtime/<run-id>. Arguments and environment can reference {runtimeDir}.

KeepOnFailure defaults true so corrupt/interesting state survives investigation. KeepOnSuccess can be enabled when the produced state itself is evidence.

## Input identity

Inputs records package-local files that define the workload. Hash only files whose content identity matters enough to justify the cost. ExpectedSha256 turns identity into a hard requirement.

input-manifest.json records the frozen job manifest hash, executable hash, declared input files, and materialized runtime seed hashes.

## Workload validation

CorrectnessChecks and EvidenceRequirements use artifact checks with result/package/runtime scopes. Supported checks include:
- existence
- minimum byte size
- minimum visible non-black pixel ratio for non-interlaced 8-bit PNGs
- optional normalized PNG region and RGB brightness threshold
- a 64-bit PNG scene difference hash with a bounded Hamming distance
- SHA-256
- contains text
- exact text

`MinimumNonBlackPixelRatio` is opt-in and ranges from 0 to 1. Use it for
screenshots captured at a point where visible gameplay is required. The check
fails closed for malformed or unsupported PNGs; jobs that do not declare it
retain their existing artifact behavior.

`ImageRegion` limits the pixel ratio to normalized `X`, `Y`, `Width`, and
`Height` coordinates. `NonBlackPixelThreshold` changes the match from any
nonzero RGB channel to any RGB channel greater than the declared 0–255 value.
Use both when a whole-image non-black check could confuse a menu, loading
screen, or dialog with the intended gameplay state.

`ExpectedImageDHash` is a 16-digit hexadecimal difference hash. Pair it with
`MaximumImageHammingDistance` from 0 through 64. `ImageRegion` may restrict the
hash to a stable HUD or scene region. The runner reports the actual hash and
distance when a check fails, so a diagnostic attempt can be visually reviewed
before its value is pinned in a new immutable test revision. A visible-pixel
check is not a scene check.

Input-driven benchmark plans that start a measurement segment are rejected at
definition load unless their correctness contract fingerprints
`screenshots/recording-start.png` with a distance limit no greater than 16.
This prevents a menu, loading screen, or flyover from producing
comparison-eligible measurements merely because it is visible. Use a tight
region and tolerance established from separately reviewed good occurrences;
do not raise the tolerance to admit a known wrong scene.

ReportedMetrics extracts finite numeric values from JSON artifacts using dot-separated object-property paths. These measurements feed the compare command.

## Experiment contract

Experiment.Id groups attempts. Variant identifies the candidate/baseline/category within that experiment.

VariedFactors should contain only intended differences. ControlledFactors document what must remain stable conceptually. The runner does not infer that two arbitrary strings prove environment equality; the input manifest and host inventory provide the evidence needed for review.

Comparison eligibility can require:
- completed execution
- passing correctness
- complete evidence
- no operator/host intervention
- no diagnostics

Ineligible attempts remain visible and retain reasons.

## Benchmark operation policy

Operations.Mode=benchmark blocks intrusive HTTP actions by default:
- live preview / manual screenshot
- manual controller input
- pause/resume
- ad-hoc diagnostics
- bulk file transfer

Job-authored plan steps still execute. If a job deliberately includes diagnostics, Experiment.AllowDiagnostics determines whether the attempt may be compared.

## Measurement segments

segment_start / segment_end create named measurement windows. The current segment is stamped into metrics.csv and boundary events are written to segments.jsonl.

Use wait_for_artifact for deterministic host-visible readiness instead of arbitrary sleeps when the workload can produce marker files.

Use `wait_for_scene` when a fresh-start input sequence can finish before a slow
host has reached the intended measurement scene. The step captures transient
live previews until its result-scoped image fingerprint matches, then publishes
the matching image at `Condition.Path`. An input benchmark must place this step
immediately before each `segment_start` and declare the same path as a workload
correctness check. The maximum image Hamming distance may not exceed 16. A
timeout fails the plan, reports the last observed mismatch, and preserves that
frame beside the requested result image with a `.last-mismatch` suffix. Timing
never starts from a loading screen or an unrelated menu.

`wait_for_scene` observes only. It does not add gameplay input, infer progress
from nominal FPS, or make a scene match less strict on a slower host.

## Preserved targets

When PreserveTargetOnRunnerError is enabled, runner/control faults may leave xemu alive. The package stays owned in Testing and QMP/input remains available. POST /api/v1/xemu/quit requests a controlled shutdown. Once xemu exits, the runner finalizes the hold and archives the package.

On runner restart, a matching live PID/start-time takes precedence over a final result file and remains held.

## Offline comparison

Use:

```sh
xemu-test-runner compare --experiment <id>
```

Only attempts whose assessment says comparison=eligible contribute numeric measurements. The command reports count, mean, min, max, and sample standard deviation per variant/metric.

It deliberately does not declare a winner or statistical significance. A later analysis layer can add paired/interleaved experiment statistics without weakening the eligibility gate.

## Current limits

- ControlledFactors are documentation plus evidence, not yet an automatic cross-run manifest-diff policy.
- wait_for_artifact is host-visible file based; guest-native progress signaling still requires guest/test integration.
- wait_for_scene is a visual readiness gate. It proves the declared image was
  observed before measurement, not that the guest consumed every earlier input.
- xemu controller automation remains keyboard/X11/SendInput based until emulator-side acknowledged controller control exists.
- physical Xbox adapters are not implemented.
- comparison aggregation is descriptive; paired experiment scheduling/interleaving remains future work.
