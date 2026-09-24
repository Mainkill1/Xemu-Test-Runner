# Screenshot diagnostics

Screenshots are diagnostic evidence by default. A performance run does not fail merely because two hosts capture different guest frames or because a diagnostic screenshot is dark/missing.

A plan screenshot:

```json
{"Type":"screenshot","Name":"steady-race"}
```

has effective purpose `diagnostic`. Any workload image check that targets that named screenshot remains visible as a diagnostic annotation but does not contribute to correctness or evidence completeness.

A test whose purpose really is visual correctness must opt in:

```json
{"Type":"screenshot","Name":"render-validation","Purpose":"correctness"}
```

That preserves the declared correctness/evidence checks for the image.

## Context sidecar

For `screenshots/steady-race.png`, the runner attempts to retain:

```text
screenshots/steady-race.png.context.json
```

The sidecar records the selected provider, host elapsed start/end/duration, active measurement segment, nearest guest-frame samples observed before/after capture, last controller input and deltas since that input.

Guest progress is best effort. By default the runner looks for `guest-frames.log` in the run result directory. A saved test can set `Workload.GuestProgressPath` to another result-relative log. Expected records use:

```text
timestamp_us=247003331 frame=14820 delta_us=16667
```

No background guest sampler is added. The runner reads at most the final 64 KiB of that file only when an input or screenshot is already happening. If no usable sample exists, the sidecar records guest context as unavailable rather than inventing a frame or timestamp.

The two samples around a capture form a bound, not an exact synchronization claim. For example, a screenshot may be described as occurring between guest frames 14820 and 14821. Host and guest deltas from the last input explain why Windows and Deck can show different images after the same wall-clock plan delay.

## Failure behavior

A diagnostic screenshot provider failure is retained as a sidecar error and the plan continues. It does not by itself fail execution, correctness, evidence or comparison eligibility.

A screenshot with `Purpose: "correctness"` keeps fail-closed capture behavior: a provider/capture failure aborts that plan path and its declared image correctness checks remain authoritative.

Manual screenshot API calls still report capture errors to the caller; the tolerant behavior applies to diagnostic screenshot steps in a saved test plan.

The image viewer automatically loads the sidecar when available and displays purpose, segment, guest frame/time and last-input distance above the image. Raw PNG and JSON evidence remain unchanged.
