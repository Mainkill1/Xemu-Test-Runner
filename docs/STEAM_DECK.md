# Steam Deck setup and attribution captures

Run the test runner as the desktop user from SteamOS Desktop Mode. xemu, automated XTest input, and X11 capture must share the graphical session. An SSH login does not automatically inherit that session.

## Discover the desktop environment

```sh
uid=$(id -u)
export XDG_RUNTIME_DIR="/run/user/$uid"
export DBUS_SESSION_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/bus"
export DISPLAY=:0
export WAYLAND_DISPLAY=wayland-0
export XAUTHORITY="$(find "$XDG_RUNTIME_DIR" -maxdepth 1 -type f -name 'xauth_*' -print -quit)"

test -n "$XAUTHORITY"
test -r "$XAUTHORITY"
```

The exact display name and authorization file can change after a reboot. Read them from the active desktop session instead of permanently hard-coding a stale file.

Start the runner with those variables in its own environment. Job-level `Environment` values affect xemu, while external screenshot tools inherit the runner's environment.

## Screenshot fallback

The tested xemu build did not advertise the QMP `screendump` command. SteamOS Spectacle also returned success from a remote session without creating the requested file. The runner detects the missing output and rejects that capture.

`ffmpeg` X11 capture produced a real PNG reliably under XWayland:

```json
{
  "XemuControl": {
    "ScreenshotProvider": "auto",
    "ScreenshotExecutable": "/usr/bin/ffmpeg",
    "ScreenshotArguments": [
      "-hide_banner", "-loglevel", "error", "-y",
      "-f", "x11grab", "-video_size", "1280x800", "-i", ":0",
      "-frames:v", "1", "{path}"
    ]
  }
}
```

Adjust `-video_size` when the desktop resolution differs. This captures the X11/XWayland desktop. Use RenderDoc for a renderer-frame capture; a desktop PNG does not expose draw state.

## `perf` capture

Check availability and policy first:

```sh
xemu-test-runner tools
cat /proc/sys/kernel/perf_event_paranoid
```

The runner attaches `perf record` to the xemu PID it launched. A diagnostic plan can pause xemu, attach `perf`, resume for an exact unpaused duration, pause again, emit `perf.data` and a text report, then quit cleanly. See [`jobs/example-linux-perf`](../jobs/example-linux-perf/README.md).

Use `CallGraph: "dwarf"` for ordinary release builds. A profiling build with `-fno-omit-frame-pointer` can use `CallGraph: "fp"` for cheaper and usually more dependable unwinding.

For xemu translated-block attribution, launch xemu with `-jitdump`, set the perf diagnostic's `ClockId` to `1`, and post-process the resulting capture with `perf inject --jit`. Leave `ClockId` unset for ordinary captures so perf retains its host default clock.

The diagnostic is intentionally marked as operator intervention because pause/resume and sampling alter execution. Use a separate uninstrumented run for performance acceptance.

## Cleanup checks

After a bounded run, verify that xemu, the runner, and `perf record` are gone:

```sh
pgrep -af 'xemu|XemuTestRunner|perf record' || true
```

The final plan step should normally be `quit`. xemu may close QMP before replying; the runner treats that as success only after the owned process actually exits.
