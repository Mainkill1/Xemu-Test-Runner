# Process-specific Linux debugger attachment

`scripts/runner_debug_launch.py` is an explicit **diagnostic-only** package
launcher for a target owned by XemuTestRunner. It permits the owning runner and
its descendants to attach a debugger to that one target, then replaces itself
with the real executable. Target PID, argument boundaries, environment, native
exit status and signals survive `exec`. The runner service requires no upgrade.

This addresses Yama's restricted attachment relationship; it does not override
admin-only or no-attach modes, ordinary UID/dumpability rules, another Linux
security module, or unavailable tools. It grants no elevated privilege and
changes no system setting. It uses a specific runner ancestor PID rather than
`PR_SET_PTRACER_ANY`. The target must be a nonprivileged executable.
[Linux kernel contract](https://docs.kernel.org/admin-guide/LSM/Yama.html).

## Package use

Copy the launcher into a diagnostic package and declare it executable. Keep
`xemu` as a separate required, hash-pinned input with `role: emulator`; the
launcher hash is not the emulator build identity.

```json
{
  "executable": "runner_debug_launch.py",
  "arguments": ["--", "xemu", "-config_path", "xemu.toml"],
  "requiredFiles": ["xemu"],
  "inputs": [{"path": "xemu", "role": "emulator", "hash": true}],
  "operations": {"mode": "smoke", "allowDiagnostics": true}
}
```

Use the maintained HTTP client to upload/reuse and submit the package.
The launch prints `XEMU_DIAGNOSTIC_PTRACER` with target/target PID/owner PID to
stderr before executing. Only an actual ancestor whose executable is
`XemuTestRunner`, or `dotnet XemuTestRunner.dll`, is accepted. Unrelated names in
later arguments do not qualify. Missing ownership, unreadable/racing ancestry,
malformed/cyclic ancestry or a rejected `prctl` fail before executing the target.
There is no fallback permission change. Another process-specific ptracer
declaration made by the target can replace this one.

Use the existing external diagnostic recipe for GDB; retain raw stderr and the
recipe result when attaching fails. Matching DWARF remains required to inspect
typed emulator state. The ownership consent is explicit at startup; benchmark
definitions keep their usual launcher and diagnostic policy.

## Verification

```sh
python3 -m unittest discover -s tests -p test_runner_debug_launch.py -v
```

Tests cover native and dotnet ancestry, misleading arguments, unrelated owners,
cycles, and refusal before target execution. A native Steam Deck HTTP job also
verified attachment and matching emulator identity after `exec`. Its raw native
evidence and original denied-attach attempt belong to
[the owning xemu issue #233 report](https://github.com/Mainkill1/xemu/blob/research/issue233-output-ownership/docs/evidence/issue233-output-ownership-20261002/README.md).
No native benchmark or emulator speedup is claimed by this tool.
