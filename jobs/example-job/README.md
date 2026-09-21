# Example job package

Copy the xemu build being tested into this directory before placing the package in the queue.

Windows:

    xemu.exe

Linux:

    xemu

For Linux, change `Executable` in `job.json` from `xemu.exe` to `xemu` and preserve the executable bit.

The package is intentionally missing an xemu binary in Git. A real queue item is not valid until its configured executable is present beside `job.json`.
