# Shared native controller foundation

`XemuTestRunner.Control.Gamepad` provides the reusable controller state, native
provider, and per-call OS-submission receipt. `native/gamepad` implements the
protocol using Windows InputInjector or Linux uinput. It does not patch xemu,
install a custom driver, or require authoring media workers.

The normal publish scripts build the helper alongside the runner. Linux
packages contain `tools/xemu-gamepad` and `tools/linux-sdl-mapping.txt`.
Windows x64 packages contain `tools/xemu-gamepad.exe`. Build a Windows ARM64
helper separately for an ARM64 host; the helper's architecture follows the
host OS, even if xemu is an emulated x64 process.

`CreateAsync(1)` must complete before `IsReady` is true. `IsAvailable` only
means the helper file exists. `ApplyStateAsync` and `NeutralizeAsync` return
sequence-matched receipts. A receipt confirms OS submission, not xemu or guest
consumption. The native helper removes its device after 250 ms without a fresh
valid state; a replay owner must refresh both held and neutral states until it
disposes the session. That ownership and the runner's test-plan dispatch are
separate integration work, so current saved `button` steps remain keyboard
backed until explicitly migrated.

The provider's `ConfigureTarget` sets only the target process environment:
`SDL_JOYSTICK_RAWINPUT=0` on Windows or the bundled
`SDL_GAMECONTROLLERCONFIG` mapping on Linux. It must be applied to the actual
xemu launch, and one OS-visible pad must have an unambiguous binding.

Verification:

```text
dotnet run --project tests/GamepadProviderChecks -c Release
cmake -S native/gamepad -B gamepad-build -DCMAKE_BUILD_TYPE=Release
cmake --build gamepad-build --config Release
ctest --test-dir gamepad-build -C Release --output-on-failure
```

The managed checks use a pipe fixture and do not qualify real hardware. The
controller workflow checks independent SDL/XInput readback where its CI host
supports it. Real xemu gameplay on the intended Windows and Deck hosts remains
a separate qualification gate.
