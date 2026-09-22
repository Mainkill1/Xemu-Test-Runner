# Worker handoff

Branch target: `feature/reliability-and-evidence`.

The code is intended to be polished and qualified rather than redesigned. Start with these gates:

1. `dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release`.
2. `dotnet run --project tests/RunnerChecks -c Release`.
3. `python -m py_compile tools/renderdoc_session.py tools/renderdoc_analyze.py scripts/check-browser.py`.
4. Run `scripts/check-browser.py` with Chromium/Playwright.
5. Run `xemu-test-runner tools` on the Windows and Linux rigs and save output.
6. Run a direct smoke package, then `jobs/example-diagnostic` after replacing its private snapshot/config values.
7. Validate WPR, perf, RenderDoc xemu-hotkey, hang bundle, pmemsave, monitor/QMP, symbolization, and external-tool recipes independently before chaining them.
8. Force cancellation/failure during each heavyweight diagnostic and confirm xemu/input/tool processes do not remain stuck.

Areas most likely to need version-specific polish:

- RenderDoc Python signatures and module path discovery.
- WPR built-in profile naming on the installed Windows Performance Toolkit.
- perf callgraph mode/permissions.
- GDB core-dump limits and path quoting.
- Target process stdout/stderr when launched through RenderDoc ExecuteAndInject.
- xemu F10 guest-frame capture behavior under the exact selected renderer/build.
- Windows foreground-input policy and Linux X11/XWayland focus.
- actual 10 GB+ HTTP transfer behavior.

Do not weaken the evidence semantics to make a test green. A completed process, responsive QMP socket, or opened RenderDoc capture is not by itself Xbox hardware correctness.


# Trustworthy experiment runner handoff

Branch: `feature/trustworthy-experiment-runner`

Stacked PR: #5, based on PR #3.

The implementation should now be treated as feature-complete for this design slice. The next worker should polish/validate rather than redesign.

## Implemented behavior to validate first

1. Build a package with `RuntimeState` and confirm the seed file is copied into `workspace/Runtime/<run-id>`, xemu is pointed at the private state through placeholders, and the seed is untouched.
2. Use `Inputs` with hashes and confirm `input-manifest.json` records the frozen job/executable/input identities.
3. Produce host-visible guest markers and exercise `wait_for_artifact` readiness.
4. Use `segment_start` / `segment_end` and confirm the segment is visible in both `segments.jsonl` and `metrics.csv`.
5. Produce a real guest-results JSON, exercise correctness/evidence checks, and confirm `assessment.json` separates execution/correctness/evidence/comparison.
6. Run repeated baseline/candidate attempts with `Experiment.Id` and `ReportedMetrics`, then run `xemu-test-runner compare --experiment <id>`.
7. During `Operations.Mode=benchmark`, confirm manual preview/input/pause/diagnostics/file/evidence-transfer endpoints return 409 while the job-authored plan still runs.
8. Force a runner/control error with `PreserveTargetOnRunnerError=true`; verify xemu remains controllable, package remains in Testing, `POST /api/v1/xemu/quit` works, and the runner archives the package when xemu exits.
9. Upload the same destination concurrently and confirm the second write is rejected. Exercise `X-Content-SHA256` success and mismatch behavior.
10. Put an incomplete package alphabetically before a valid stable package and confirm the later valid package can still be claimed.

## Current CI state

The current implementation builds without warnings on both Windows and Linux and browser fixtures pass.

The legacy regression suite has one expected stale assertion:

```text
ambiguous attempt is held, finalized attempt is archived
```

That assertion expects the old rule that a durable result wins before checking ambiguous/live process ownership. The implementation intentionally changed this: process ownership now takes precedence so a preserved/live target cannot be archived or replayed merely because result.json already exists.

Update that regression expectation during polish; do not restore the old behavior just to make the test green.

## Important remaining limits

- Guest-native signaling is not implemented by the runner; `wait_for_artifact` depends on host-visible output from test software/extraction tooling.
- ControlledFactors are declared and evidenced, but automatic cross-run manifest-diff enforcement is not yet implemented.
- Comparison aggregation is descriptive only; it does not perform paired/interleaved statistical significance analysis.
- Physical Xbox execution adapters remain future work.
- Host keyboard/X11/SendInput controller automation remains a compatibility layer until xemu exposes acknowledged controller commands.
