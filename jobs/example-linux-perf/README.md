# Linux perf example

This package attaches `perf record` to the launched xemu process, resumes xemu for a bounded ten-second window, retains `perf.data` plus a text report, and exits xemu through QMP.

Before staging it:

- Copy the exact Linux xemu build into this directory as `xemu.AppImage`, or adjust `Executable` and preserve its executable bit.
- Configure `xemu.toml` with your local firmware, HDD and game paths.
- Run the test runner from the same graphical desktop session as xemu. X11/XWayland input and external screenshots also require the session's `DISPLAY` and `XAUTHORITY`.
- Confirm `xemu-test-runner tools` reports `perf` available and that the host's `perf_event_paranoid` policy permits attaching to a process owned by the same user.

The diagnostic deliberately pauses and resumes xemu to define the capture window. Its result is marked `operator_intervened`; use it for attribution, not as an ordinary frame-time benchmark. For cheaper frame-pointer unwinding, build xemu with frame pointers and change `CallGraph` to `fp`.
