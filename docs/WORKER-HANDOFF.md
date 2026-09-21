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
