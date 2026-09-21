# Example build package

Copy the exact candidate xemu build and all of its runtime dependencies into this directory before staging it in Pending. No xemu binary, firmware, or game image is distributed in this example.

The supplied `job.json` targets Windows and expects `xemu.exe`. For Linux, change **both** `TargetOs` to `linux` and `Executable` to `xemu`, and preserve the executable permission bit.

Add the firmware/disk/test paths required by your xemu installation to `xemu.toml`. Add any package-local inputs you require to `RequiredFiles` in `job.json`. `ExpectedExecutableSha256` is optional; when supplied, it must match the candidate binary.

Stage the complete folder as `Queue/Pending/.incoming-build-123`, run `validate` on it, then rename it to `build-123`. Never copy a partly assembled build into a visible queue directory.
