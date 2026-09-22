# Experiment contract example

This is a schema/example package, not a ready-to-run xemu workload.

It demonstrates:
- benchmark operation policy
- experiment ID/variant and controlled/varied factors
- input identity
- correctness/evidence contracts
- numeric reported metrics
- host-visible readiness with wait_for_artifact
- named measurement segments

A real workload must arrange for guest/test tooling to produce guest-ready.json and guest-results.json under the run result directory. If your guest cannot write host-visible results directly, add an extraction step/tool that creates those artifacts before post-run evaluation.

For mutable HDD or other writable state, enable RuntimeState and add seed files, then point the packaged xemu configuration/arguments at {runtimeDir}.
