# Built-in OS gamepad completion

**Goal:** Finish real, external full-state controller injection without a custom driver, xemu patch, DLL interposition, or process-local SDL virtual joystick.
**Architecture:** Keep the existing small Windows InputInjector/Linux uinput workers. Prove OS readback independently, then expose bounded managed supervision. A provider receipt means OS submission, not guest consumption.
**Scope:** Controller subsystem of issue #65 / PR #66; the separate media, HTTPS UI, queue and draft feature gates remain visible.

## Work and checks

- [x] Reproduce Windows SDL disconnect from retained run 36290185533. Compare direct XInput with SDL2/SDL3 and check SDL backend/session selection; never change injection data to satisfy a test.
- [x] Fix fixture cleanup masking startup errors. First add a pipe-only regression for early exit and idempotent disposal; retain native stderr.
- [x] Qualify the existing adapter on Linux and Windows 11 ARM64 with independent readback. Add Windows x64-under-emulation and additional teardown tests to CI; retain their actual per-head results. Server capability rejection is not positive controller qualification.
- [x] Implement managed `IXemuGamepadProvider` supervision, serialized full-state requests, bounded receipts/errors and explicit neutralization. No automatic non-neutral heartbeat that could keep stale browser input alive; input freshness remains enforced natively.
- [x] Record exact target SDL environment and Linux mapping, native deployment, OS/capability restrictions, and input scope. No global SDL environment mutation, privileged auto-relaunch or driver installation.
- [ ] Verify the latest expanded native/managed checks and update issue/PR with measured results and remaining full-feature gates.

## Review focus

Wrong SDL backend / remote desktop, producer vs consumer process separation, startup error preservation, stale-input deadline despite blocked output, and target configuration applied only to selected xemu launches.
