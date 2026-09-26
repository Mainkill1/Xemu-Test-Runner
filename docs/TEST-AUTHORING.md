# Test Authoring

Test Authoring is the planned interactive path for creating reusable xemu tests through a secure browser session. It is tracked by [issue #65](https://github.com/Mainkill1/Xemu-Test-Runner/issues/65).

Phase 0 defines architecture, state, input, window-coordinate, and provider contracts. It does not yet expose runtime routes or start a media worker.

## Three separate operating paths

### Test Authoring

Purpose: create, record, edit, replay, validate, and publish a saved test.

Required capabilities:

- trusted HTTPS browser origin;
- playable low-latency WebRTC video from the owned xemu window;
- browser physical-gamepad input;
- native complete Xbox-compatible controller injection;
- host-applied input recording with frame sequence;
- xemu-owned window mouse, scroll, drag, and host key control;
- headless replay without WebRTC;
- immutable saved-test publication.

A session is either fully interactive and qualified or it fails to start. Screenshot polling is not an authoring fallback.

### Lightweight Control

Purpose: inspect or recover the currently owned xemu target.

The existing `/control` path remains responsible for:

- direct screenshots and lightweight preview;
- pause/resume and quit;
- emergency/manual controller input;
- unstick/recovery actions;
- future static-image xemu-window pointer and key controls.

Control operations remain governed by the active job's operation policy and are retained as intervention when used during execution. They do not start a continuous encoder.

### Automated Test Execution

Purpose: reproduce a saved test and collect authoritative evidence/results.

Normal execution must not start:

- real-time capture encoding;
- WebRTC media or data peers;
- browser sessions;
- authoring audio transport.

Frame-aware replay uses only a lightweight presented-frame observer and native input provider. Direct screenshots remain the correctness/evidence path.

## Ownership rule

Only one top-level activity owns the runner:

```text
Idle
  -> TestExecution
  -> Idle

Idle
  -> TestAuthoring
  -> Idle
```

Queued work waits while authoring owns the runner. Authoring is refused while execution owns it. This is not controlled by a job-level `AllowRealtimeStream` override.

## Authoring readiness

A future session reaches Ready only when all of these are true:

- owned xemu process and QMP are ready;
- capture target belongs to the owned xemu process;
- capture/encode/decode rates pass the configured floor;
- browser receives and decodes the media track;
- both WebRTC data channels are open;
- native virtual gamepad is ready;
- frame progress is observable;
- controller state age/round-trip passes the configured limit;
- browser is a secure context with a mapped standard gamepad.

Failure of any hard gate blocks authoring.

## Input timing

Recorded controller state is authoritative only after the host virtual-device provider accepts it. Each transition carries:

- complete buttons, triggers, and stick values;
- host-applied monotonic timestamp;
- presented-frame sequence;
- minimum elapsed milliseconds;
- minimum presented frames;
- timeout.

Replay advances only when both the elapsed-time and frame-progress requirements pass. A frozen/slow target that misses frame progress fails explicitly.

## Xemu window grid

Pointer actions use a process-bound integer grid:

```text
X: 0..10000
Y: 0..10000
```

The grid maps to the current xemu-owned client surface, not the physical monitor. Every action carries the expected process ID, window ID, and surface version. A stale screenshot or resized/replaced window is rejected before input is applied.

Supported planned actions include move, click, double-click, button down/up, drag path, wheel scroll, and host key chord. These are intended for older xemu snapshot/reload/configuration interfaces as well as authoring recovery.

## HTTPS and certificates

The planned authoring worker serves a dedicated Kestrel HTTPS origin on a configurable port. It requires an operator-supplied trusted certificate or documented trusted development certificate provisioning. Certificate passwords are provided through an environment/secret source rather than committed JSON. There is no HTTP downgrade.

The existing HTTP control plane can link to the secure authoring origin without being replaced in the first implementation.

## Platform qualification boundary

The complete feature requires real-host qualification for:

- Windows 10/11 window capture, DPI changes, modal windows, H.264 encoding, and a maintained virtual Xbox-compatible device;
- Linux X11 capture/input;
- Wayland/Steam Deck ScreenCast/PipeWire capture and qualified input path;
- Linux uinput/libevdev virtual controller;
- browser/controller disconnect and worker-crash neutralization;
- no-media assertions during normal execution.

A platform/backend that cannot meet the contract is reported unsupported. It does not activate a lower-fidelity authoring mode.

## Design and implementation plan

- [Detailed design](superpowers/specs/2026-09-26-realtime-test-authoring-design.md)
- [Phase 0 implementation plan](superpowers/plans/2026-09-26-realtime-test-authoring-phase-0.md)
