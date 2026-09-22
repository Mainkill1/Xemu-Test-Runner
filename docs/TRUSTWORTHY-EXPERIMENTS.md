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
- SHA-256
- contains text
- exact text

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
- xemu controller automation remains keyboard/X11/SendInput based until emulator-side acknowledged controller control exists.
- physical Xbox adapters are not implemented.
- comparison aggregation is descriptive; paired experiment scheduling/interleaving remains future work.
