# Test Authoring runtime implementation

**Goal:** Turn #65 / #66 into executable authoring functionality, not another contract-only milestone.

**Architecture:** Preserve the existing control plane. Bind an optional authenticated HTTPS authoring host to the existing queue, test library, and target ownership. Run real media in a supervised external worker; replay never starts that worker. Share native controller/window input between authoring and replay. Keep dependency qualification separate from guest correctness.

**Execution:** Inline implementation, tests before behavior, batch commits, exact-head CI. Do not merge or describe missing functionality as complete. Existing designs remain authoritative except for the corrections below.

## Corrections to Phase 0

- Captured/encoded/decoded frames are not xemu presents or guest input acknowledgments. Never feed those counters to the replay scheduler. A qualified frame observer must identify its source and epoch. A 30 FPS game, a static menu, or paused VM is not automatically a failed 60 FPS transport.
- State transitions alone do not enforce readiness. Runtime transitions into Ready/Recording must consume current validated readiness, and media shutdown must precede replay.
- Window IDs, PID and surface versions are session-local. Persist logical target/layout constraints, then bind fresh identities on replay. Grid scaling alone does not make a differently laid-out menu compatible.
- Native input acknowledgment proves host application, not that the Xbox guest consumed the input. Record/check outcomes separately.
- Do not silently apply the 250 ms / 15-frame button floor to every analog sample. Preserve authored trajectories and retain explicit timing-policy identity. Coordinate with #63 / #64 rather than adding another unrelated global pacing setting.
- Full-state unreliable packets repair stale state but cannot guarantee a short press survives loss. Preserve reliable button transitions, bounded queues, host receipts, and explicit failure on an unrecordable gap.

## Implementation sequence

1. Regression checks: real queue mutual exclusion, durable restart hold, authoritative input recording, trace validation, draft revision control, guarded pointer mapping, and bounded worker protocol.
2. Queue admission and persistent authoring ownership; retain exclusion until both target and worker exit are confirmed.
3. Mutable draft storage, immutable trace identity, validation receipts, and publication through the existing saved-test library. Saving is never execution permission.
4. Native gamepad/window input and replay scheduler; no pixels or media in execution. Retain cleanup failure and neutralize on every exit path.
5. Supervised real-time worker, bounded IPC, WebRTC signaling and health. No PNG transport or success stub.
6. Optional HTTPS host, authentication, capabilities and same-origin UI. Draft creation/editing, explicit launch/record/replay/publish, physical controller mapping, window mouse/grid/key controls.
7. Integration with existing test parser/executor and lightweight /control. Every manual test-time action respects policy and records intervention.
8. Test actual HTTPS/API/browser/native worker boundaries, run existing suites and publish checks, and inspect the final diff. Record source gaps and real-host qualification honestly.

## Merge gates

- Existing runner behavior remains unchanged when authoring is disabled.
- Normal execution cannot race with or retain a media worker.
- No arbitrary desktop target, stale-coordinate click, unbounded IPC queue, or implicit replay/publication.
- A published trace preserves analog/overlapping inputs and can be replayed without a browser.
- Source-level integration is demonstrated by tests; native xemu/driver latency evidence is not fabricated from fakes.
