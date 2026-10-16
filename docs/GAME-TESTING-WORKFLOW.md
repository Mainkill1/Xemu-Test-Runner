# xemu game testing workflow

This is the reference procedure for repeatable game testing through Xemu Test
Runner. It covers firmware qualification, game/account setup, performance
measurement, diagnostic profiling, evidence review, and test-device cleanup.

The central rule is simple: prepare guest state separately, prove the intended
scene before timing begins, and keep release measurements separate from
diagnostic runs.

## 1. Freeze the test identity

Record full SHA-256 values before creating a job:

- firmware/BIOS image and memory profile;
- xemu executable and renderer/backend;
- MCPX boot ROM;
- EEPROM;
- game ISO/XISO;
- immutable HDD seed or snapshot carrier;
- runner request and any input procedure;
- profiler/plugin and symbol list for a diagnostic run.

Never identify an input only by a filename, release label, shortened hash, or
the fact that it exists on both hosts. A runner asset ID is stable, but the
asset's full digest is still part of the test identity.

### Encryption and signing domains

Treat an HDD, account, profile, and save as bound to the firmware/EEPROM key
domain that created it until a title-specific portability test proves
otherwise. Do not reuse a profile or save created under another BIOS merely
because the game and virtual disk format match. The safe sequence is:

1. boot the exact candidate firmware with its selected EEPROM;
2. create the account/profile/save on a private copy of a clean HDD;
3. reset and load it again with the same candidate;
4. publish that resulting HDD as a new immutable, candidate-specific asset;
5. use the new asset ID and digest in later measurements.

## 2. Separate authoring from measurement

A fresh HDD often needs dashboards, accounts, profiles, save slots, license
agreements, or first-run settings. This is **state authoring**, not a benchmark.

Use a release xemu build and the exact candidate BIOS/EEPROM. Materialize a
private HDD from a clean `xiso-seed`, set disk retention to `keep`, and give the
job a setup-only name and tags. Repeated inputs may be used when every menu's
default action is safe, but preserve intermediate screenshots. If menu timing
is uncertain, allow human input in a diagnostic-policy job.

After the intended game state is visible:

1. leave the game normally so it can save;
2. perform a controlled xemu quit;
3. retain the private runtime HDD;
4. boot that retained state once and verify the account/save loads;
5. import it into the runner's `Workspace/DiskAssets` catalog under a new ID;
6. record its origin job, run ID, BIOS hash, EEPROM hash, length, and SHA-256.

Never report setup-job timing as game performance.

## 3. Use the correct run class

| Run class | xemu build | Manual control | Profiler | Timing authority |
|---|---|---:|---:|---|
| State authoring | release | allowed | no | none |
| Release acceptance | release | blocked during segment | no | yes |
| Diagnostic function census | release-optimized diagnostic host | blocked during capture | yes | no |
| Crash investigation | matching release first; instrumented follow-up | as declared | optional | no |

Release acceptance runs must not load a tracing plugin, debugger, RenderDoc,
API interceptor, or per-I/O logger. Diagnostic output can rank code paths, but
its CPU time includes instrumentation and cannot replace release results.

## 4. Prove input consumption and scene readiness

`controller-input.json` proves that the runner submitted controller state to
the operating system. It does not prove that xemu or the game consumed it.
Likewise, fixed wall-clock waits do not prove that a slow host reached the same
menu or gameplay scene.

For every input-driven benchmark:

1. record the controller backend and mapping;
2. use generous setup input spacing;
3. add `wait_for_scene` immediately before every `segment_start`;
4. use a reviewed stable image region and an expected 16-digit dHash;
5. keep maximum Hamming distance at 16 or less;
6. declare the matching image as a correctness requirement;
7. inspect the start and end screenshots before accepting the run.

A `wait_for_scene` timeout is a failed route. Retain its `.last-mismatch`
image and controller receipt. Do not increase the Hamming distance merely to
make a different menu pass.

When a game cannot be navigated reliably by a fixed sequence, run a separate
human-position authoring job, retain the resulting private HDD or snapshot,
and build the measurement job from that reviewed state.

## 5. Define the measured interval

Place `segment_start` only after scene readiness succeeds. Capture a start
screenshot, execute the frozen workload for the declared duration, capture an
end screenshot, and close with `segment_end` before the final `quit` step.

The job should declare:

- the segment name;
- sample duration;
- minimum telemetry and frame samples;
- guest frame/flip log paths;
- correctness checks for the matched start scene and visible end scene;
- required `segments.jsonl` evidence;
- private runtime state and cleanup policy.

Use a parked or otherwise deterministic game state for host comparisons. A
moving route samples different world positions when hosts render different
frame counts and needs a separate workload contract.

## 6. Run and review

Before submission, confirm:

- the runner is idle and its graphical session is available;
- no old xemu process owns the test resources;
- firmware, xemu, boot ROM, EEPROM, media, and disk hashes match the record;
- RAM size, renderer, driver-cache policy, and power policy are declared;
- writable state is private to this attempt;
- benchmark policy blocks preview, manual input, pause/resume, diagnostics, and
  bulk transfer during the measured run.

After completion, review all four outcomes independently:

1. execution completed without a native crash or timeout;
2. correctness checks passed;
3. required evidence is complete;
4. comparison eligibility is true for the declared experiment.

Always inspect the scene screenshots and controller receipt for a new or
changed route. A completed process with a menu screenshot is not a game
performance result.

## 7. Repetition and reporting

Use independent process launches and at least two clean repeats for a pilot.
Freeze the final repetition count and acceptance rules before examining the
candidate outcome. Alternate variants in matched blocks when comparing builds.

Keep host results separate. Never average a regression on one machine away
with a gain on another. Report per host:

- exact input hashes and run IDs;
- launch-to-scene time;
- frames, FPS/cadence, and process CPU;
- frame-time average, p50, p95, p99, maximum, and sample count;
- storage or guest metrics required by the workload;
- every failed, timed-out, or comparison-ineligible attempt and its reason.

## 8. Diagnostic profiles

Function-entry counts and translated-block CPU attribution belong in a
separate diagnostic campaign. Pin the plugin, entry list, kernel, BIOS, and
xemu hashes in every request. State the profiler's limitations next to its
results. For example, an entry-instruction hit is not necessarily a completed
logical call, and nearest-symbol translated-block timing is not exact
source-function cost.

Use diagnostic profiles to form a source-level hypothesis. Verify any firmware
optimization with focused semantic tests and fresh release measurements.

## 9. Failure handling and device care

Do not silently retry a failed route with looser checks. Preserve the failed
request, screenshots, last mismatch, controller receipt, result, and crash
bundle. Diagnose the boundary that failed: asset identity, state authoring,
controller submission, guest consumption, scene readiness, measurement, or
finalization.

Avoid restarting a graphical test appliance when the desktop requires human
login. Stop or quit only through an allowed controlled path. Clean historical
packages after evidence is copied; retain games, immutable disk assets,
runner/tools, and the active campaign. Do not delete an owned runtime disk.

## Attached reference files

- [Release measurement request example](game-testing/release-game-measurement.request.example.json)
- [Candidate-specific state-authoring request example](game-testing/profile-seed-authoring.request.example.json)
- [Test record example](game-testing/game-test-record.example.json)
- [`check-game-test-record.py`](../scripts/check-game-test-record.py), a local
  preflight for hashes, key-domain provenance, release/diagnostic separation,
  and the measurement-start scene gate

Run the preflight before uploading a request:

```bash
python scripts/check-game-test-record.py \
  docs/game-testing/game-test-record.example.json \
  --request docs/game-testing/release-game-measurement.request.example.json
```

The examples use zero hashes and generic asset names. Replace every identity
and scene fingerprint with measured values; never submit the examples as-is.
