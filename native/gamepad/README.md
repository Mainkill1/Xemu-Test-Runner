# Built-in OS gamepad helper

User-mode C++20 adapter: Windows InputInjector or Linux kernel uinput.
No additional gamepad driver, SDL dependency, injected DLL or patched target.
See [controller foundation, deployment and qualification](../../docs/CONTROLLER-GAMEPAD.md).

## Host architecture, not target architecture

Build/select the helper for the **host OS architecture**. Its consumer may have
another architecture because the two processes communicate through an OS-visible
gamepad. On Windows ARM64, use the ARM64 helper even when xemu or the runner is
an emulated x64 program. There is no in-process ABI or replacement-DLL coupling.
The qualification workflow separately selects `workerArch` and consumer `arch`.

The x64 helper launched under Windows 11 ARM emulation failed to activate the
OS WinRT component with HRESULT `800700c1` (bad executable format). The native
ARM64 helper works there. This is distinct from the Server CI image's missing
WinRT class (`80040154`), which remains an explicit unavailable capability.

```powershell
# On an x64 Windows host:
cmake -S native/gamepad -B gamepad-build -A x64
# On an ARM64 Windows host, use -A ARM64 instead, in a separate build directory.
cmake --build gamepad-build --config Release
cmake --install gamepad-build --config Release --prefix out/gamepad
```

Linux installation must retain `linux-sdl-mapping.txt` beside `xemu-gamepad`.
Before launching the target, the managed provider sets only its process-specific
SDL configuration. Input is OS-visible rather than PID-confined; ownership and
binding must be established by the runner. Creating an injector can require an
authorized Windows capability/elevation context or provisioned Linux permissions.
The helper never automatically elevates privileges or installs a driver.

A native receipt proves successful OS submission, not guest consumption. Once
input begins, absence of a fresh valid state for 250ms removes the device, even
if the protocol thread is blocked writing output. The caller must explicitly
refresh a replay hold; stale browser samples must not receive invented refreshes.
