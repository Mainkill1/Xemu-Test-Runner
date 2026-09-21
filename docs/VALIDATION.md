# Validation record — five reliability additions

Date: 2026-09-21. Base: `413362cce601e587dc597f8a1aca47f7c04d5537`.

## Executed in this environment

- JavaScript syntax checks with Node for the reconstructed home, control and evidence pages: passed.
- Headless Chromium offline fixture checks: home statistics and intervention text, controller request generation, recorder display update, evidence links and bounded-tail rendering passed. No browser script exceptions were observed.
- Source review of queue transitions, attempt persistence, cancellable process/QMP lifetimes, range arithmetic, JSON contracts and native input layouts.
- `dotnet run --project tests/RunnerChecks -c Release` was attempted: **not executed**, because `dotnet` is not installed. No C# compilation or runtime test is claimed.

The browser test replaces `fetch` with fixtures and loads HTML directly. It does not execute the embedded HTTP listener, native input, QMP, xemu, GPU providers or the .NET test executable.

## Added regression checks — not executed here

`tests/RunnerChecks` is a console regression executable with no external test framework. It checks executable-hash/OS preflight, exclusive leases, recovery decisions including live processes, consecutive watchdog failures and cancellation, shared preview caching and failure backoff, bounded log reads and 64-bit ranges, the Win32 INPUT ABI, in-flight transfer intervention and activity accounting.

Run on both Windows and Linux:

```sh
dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release
dotnet run --project tests/RunnerChecks -c Release
```

The default tail test uses an 8 MiB local file. `-- --large-file` explicitly creates an 11 GiB logical file; on a filesystem without sparse behavior this may require that much storage. Neither is an HTTP throughput qualification.

## Native qualification still required

1. Run two processes against the same workspace and separately against different configs sharing Testing. The second must refuse ownership. Kill a runner during preflight, between launch/PID persistence, and after result persistence; verify hold/retry/archive behavior without duplicate xemu.
2. Validate native Windows and Linux builds, missing DLL/shared-library behavior, declared files, read-only/unwritable results, low space and wrong hashes. No PE/ELF architecture auto-detection is implemented.
3. Exercise real QMP availability, PNG support and renderer output; test query failure/reset, stop/cont, process exit and a real QMP-unresponsive case. A responsive QMP socket is not a game-progress oracle.
4. Cancel a held input while stopping xemu; verify key release, foreground rejection, X11 window disappearance and process shutdown. Native Wayland remains unsupported.
5. Open multiple consoles simultaneously, change active jobs, and fail screenshot capture. Check shared cadence, no old-run frame return and intervention records. Measure overhead with preview off versus on; no numerical overhead improvement is claimed yet.
6. Exercise 10 GB+ upload/download/resume, two simultaneous uploads to the same file, interrupted transfers, artifact ranges and stalled clients. No real 10 GB+ network transfer was run here.

Keep this change unqualified until actual .NET builds and native checks pass. No binaries, benchmark speedup claims, hardware-conformance verdicts or GitHub Actions runs are included.
