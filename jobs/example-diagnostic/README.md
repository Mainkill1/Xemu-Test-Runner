# Snapshot diagnostic example

This package demonstrates the intended unattended diagnostic flow:

1. Launch the candidate xemu build through RenderDoc instrumentation.
2. Load a named xemu VM snapshot while startup is paused.
3. Verify that the configured host input adapter is available.
4. Resume and capture one guest-renderer frame using xemu's F10 RenderDoc hook with PGRAPH tracing enabled.
5. Pause after capture and run a 30-second Windows WPR/GPU trace.
6. Retain a guest physical-memory range and final screenshot.

Before staging the package:

- Copy the exact `xemu.exe` and its runtime dependencies into this directory.
- Replace `SnapshotName` with a snapshot available in the package's configured HDD.
- Point `xemu.toml` at your private firmware/HDD/disc assets.
- Install RenderDoc and configure `Diagnostics.RenderDocPythonPath` if Python cannot import its `renderdoc` module.
- Install ProcDump if you want automatic Windows hang dumps.
- Adjust the memory range or remove that recipe if it is not relevant to the investigation.

The RenderDoc `xemu-hotkey` trigger deliberately uses xemu's existing guest-renderer F10 capture hook rather than generic desktop presentation capture. `LaunchMode: renderdoc` keeps a RenderDoc target-control connection alive before graphics initialization so the in-process hook can start/end capture at xemu's renderer boundary.

For Linux, use a Linux xemu executable, set `TargetOs` to `linux`, replace the WPR recipe with a `perf` recipe, and ensure xemu runs under X11/XWayland if automated keyboard input is required.
