# Crash reports, diagnostic ZIPs and queue continuation

A native crash is an execution outcome, not a reason to leave the batch in an interactive hang. Finished attempts retain their executable/package and diagnostics; the next already-requested test proceeds once target termination and archival are confirmed. No crash collector launches a replacement test.

## Files retained

```
Results/RUN_ID/
  result.json
  assessment.json
  crash/report.json
  crash/report.txt
  crash/windows-event.xml          # Windows, when available
  crash/xemu.dmp                   # matching WER dump, when available
  crash/windows-analysis.txt       # local CDB analysis, when available
  crash/systemd-coredump.json      # Linux, when available
  crash/systemd-report.txt
  crash/xemu.core                  # matching retained core, when available
  crash/gdb-backtrace.txt
  diagnostics.zip
  diagnostic-bundle.json
Tested/PACKAGE/
  xemu.exe                        # original tested executable; filename can vary
  xemu.exe.RUN_ID.diagnostics.zip
```

The ZIP is also mirrored beside a nested executable, not just the package root. Its receipt includes the ZIP SHA-256, byte count, mirror location/state and omitted-file count. `bundle-manifest.json` inside the ZIP associates the run/executable SHA with included file paths, lengths and SHA-256 digests plus explicit omissions. The executable is not duplicated inside the ZIP; it remains beside it and is identified by its hash.

Included scope is final result/assessment, configuration/launch/input/host/preflight identity, stdout/stderr, sampler CSV and event/segment logs, plus crash/diagnostics subtrees. Hidden unrelated files are not swept from the machine. Linked paths and unfinished .part/.tmp artifacts are excluded. Original OS dumps are copied, not deleted or moved out of the OS store.

A complete ZIP inventory does not mean every OS provider generated a dump: capture/dump/analysis outcomes remain separate. If full packaging fails or times out, a bounded small-metadata ZIP is attempted and marked partial. An unavailable mirror or ZIP is reported without changing a confirmed crash into a success or blocking the queue solely for diagnostics.

## Agent/API interface

```sh
python scripts/runner_tests.py diagnostics RUN_ID
python scripts/runner_tests.py diagnostics RUN_ID --out ./diagnostics.zip
```

The first command reads one compact JSON summary. The second explicitly streams the ZIP and verifies its finalized SHA-256; same-length corruption is not accepted. A partial diagnostic archive stays partial in the receipt. No raw CSV/core download is required merely to learn that an attempt crashed.

```
GET /api/v1/runs/RUN_ID/diagnostics
GET /api/v1/runs/RUN_ID/crash
GET /api/v1/runs/RUN_ID/diagnostics?format=markdown
GET /api/v1/runs/RUN_ID/artifacts/diagnostics.zip
GET /api/v1/diagnostics/crash-capabilities
```

Diagnostic summaries are bounded and remain available while another benchmark runs. Raw ZIP download uses the existing range/HEAD artifact contract and bulk-transfer policy. Job/assessment and executable-hash summaries link to the run's diagnostic view. An archived job can have state=tested and execution=crashed: archival disposition is not guest correctness.

## Classification and capture

Windows recognizes known fatal exception exit statuses and correlates Application Error events by actual PID, full image path and run time. WER LocalDumps are imported only after validating native dump PID/image identity. Existing OS configuration is read, not modified. CDB postmortem analysis is optional and uses the local executable directory for symbols.

Linux direct launch can retain native waitpid exit/signal status through the included, local `tools/runner_posix.py` helper. It does not run a shell, debugger, scheduler or network service. The engine still owns the actual target PID for telemetry/control. The helper execs only an already-authorized target after a bounded handshake; it never invents another attempt. Python/helper availability is visible. With the adapter unavailable or RenderDoc launching the process, exit-code-only reporting remains explicit and does not guess that exit(139) equals SIGSEGV.

The Linux collector matches systemd-coredump metadata by PID, executable path, boot identity and timestamp, then optionally exports the retained core and runs GDB after target death. GDB initialization/auto-load and network debuginfod lookup are disabled for this automated analysis. Missing tools, permissions, expired cores, host truncation and size/time limits remain report issues. Non-systemd crash handlers require a future adapter or explicitly configured package-local ReportFiles; they are not silently treated as supported.

Runner-requested termination is not classified as a native crash from its exit code/signal. A matching OS crash event can still identify a preceding native failure. Ordinary nonzero exit stays failed; timeout stays timedOut; cancellation stays cancelled. SIGKILL/TERM alone are not segmentation faults or proof of OOM. A guest crash while xemu stays alive remains a guest/workload failure.

Captured OS crash identity is retained even if subsequent dump export or analysis fails. Early native crashes remain in executable-hash history when monitoring never initialized; they are ineligible, not omitted as if the build passed. Stored baseline/build record schemas remain unchanged.

## Bounds and batch defaults

`Diagnostics.CrashReports` controls capture, ZIP and explicit application-report paths. Defaults: 15 s postmortem capture, 15 s ZIP work, 2 s wait for a provider record, 512 MiB per artifact, 1 GiB total uncompressed archive input, 256 files. Metadata fallback and its mirror each have a separate 2 s bound; diagnostic subprocess cleanup also has bounded waits. These are application-level limits, not hard real-time guarantees for a failed kernel or storage device.

`Reliability.PreserveTargetOnRunnerError` now defaults to false. Existing configuration files explicitly setting it to true retain the interactive preservation behavior; set it to false for unattended batches. The existing watchdog/live-hang bundle is different from post-crash collection. Legacy diagnostic process/output draining now shares one deadline, so a descendant-held stdout pipe cannot wait forever after the main tool exits. Large text outputs fail explicitly rather than growing without bound.

A genuinely unkillable target, corrupt ownership journal, failed mandatory evidence write or filesystem that cannot archive the package is still an infrastructure problem. The runner must not falsely report termination and start overlapping benchmarks. Those cases need explicit recovery, not deletion of ownership evidence. Normal confirmed crashes, missing dump providers and confirmed-terminated timeouts are not such holds.

## Prerequisites / validation boundary

WER LocalDumps must already be configured for the target identity; Windows applications with custom handlers may need ReportFiles or a future dedicated handler adapter. Linux needs an enabled, accessible core provider for a core file; Python alone provides native exit identity, not core generation. Matching PDB/DWARF/module symbols are needed for useful function/source analysis. Tool presence alone does not certify these requirements.

The crash qualification workflow runs real isolated fatal processes through the actual RunnerEngine on Windows/Linux, verifies the next queued test completes, and retains the source revision and diagnostic proof. Support checks cover missing collectors, stale reports, output/time limits, ZIP omissions/hashes and failed destinations. Hosted-fixture success does not certify every WER/systemd configuration, symbol format, GPU-driver crash or real xemu workload on the test rigs.
