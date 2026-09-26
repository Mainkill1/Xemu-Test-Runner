# Real-time Test Authoring Phase 0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish buildable, tested contracts for exclusive authoring ownership, authoring session readiness/state, complete controller samples, frame-aware replay gates, and process-bound xemu window coordinates without changing current runner behavior.

**Architecture:** Add a focused `XemuTestRunner.Authoring` namespace containing only pure contracts, state/lease logic, and platform-provider interfaces. A new console check project exercises failure-prone invariants on Windows and Linux through the existing CI matrix. No media, driver, HTTPS, route, worker, or queue behavior is wired in Phase 0.

**Tech Stack:** .NET 10, C# nullable reference types, existing no-framework console checks, GitHub Actions Windows/Linux matrix.

**Spec:** `docs/superpowers/specs/2026-09-26-realtime-test-authoring-design.md`

## Global Constraints

- Test Authoring and normal test execution are mutually exclusive runner activities.
- Lightweight `/control` behavior is not replaced or changed in Phase 0.
- No real-time media dependency or route is introduced in Phase 0.
- No screenshot authoring fallback is represented in the contracts.
- Complete analog controller state is first-class; keyboard mappings are not an authoring provider.
- Replay timing requires minimum elapsed time **and** minimum presented-frame progress.
- Window coordinates use an inclusive integer 0-10000 grid and require the expected surface version.
- Window/provider interfaces accept an owned xemu PID and never expose arbitrary desktop injection.
- All new code must compile warning-free with `TreatWarningsAsErrors=true` on Windows and Linux.
- Every commit remains buildable and checkable.

## Review Focus

- A stale/disposed activity lease must not release a newer execution or authoring owner; Task 1 tests repeated disposal and reacquisition.
- Session callers must not skip readiness stages or continue from terminal states; Task 2 tests invalid transitions and failure terminality.
- Replay must not pass when only time or only frame progress is satisfied; Task 3 tests both one-sided cases.
- Grid endpoints and the documented 1920x1080 example must map deterministically; Task 4 tests 0, 10000, midpoint/example and invalid values.
- A pointer action created from an old screenshot/stream surface must fail before pixel mapping; Task 4 tests surface-version mismatch.

---

### Task 1: Exclusive runner activity contracts

**Files:**
- Create: `src/XemuTestRunner/Authoring/RunnerActivityCoordinator.cs`
- Test: `tests/AuthoringChecks/Program.cs`

**Interfaces:**
- Produces: `RunnerActivityKind`, `RunnerActivitySnapshot`, `RunnerActivityConflict`, `RunnerActivityLease`, and `RunnerActivityCoordinator`.
- `RunnerActivityCoordinator.TryAcquire(RunnerActivityKind kind, string ownerId, out RunnerActivityLease? lease, out RunnerActivityConflict? conflict) -> bool`.
- `RunnerActivityCoordinator.Snapshot() -> RunnerActivitySnapshot`.

- [ ] **Step 1: Write the failing activity checks**

Add checks named:

```csharp
await Check("execution and authoring leases are mutually exclusive", () => { ... });
await Check("disposing a stale lease cannot release a newer owner", () => { ... });
await Check("activity snapshots retain owner and acquisition time", () => { ... });
```

Assertions must prove:

- Idle cannot be requested as an owned activity.
- `TestExecution` blocks `TestAuthoring` and reports both requested/active kinds and active owner ID.
- Disposal returns the coordinator to Idle.
- Repeated disposal is harmless.
- An old token cannot release a later owner.

- [ ] **Step 2: Run the checks to verify the types are missing**

Run:

```bash
dotnet run --project tests/AuthoringChecks -c Release
```

Expected: build failure because the activity types do not exist.

- [ ] **Step 3: Implement the coordinator**

Implement exactly:

```csharp
public enum RunnerActivityKind { Idle, TestExecution, TestAuthoring }

public sealed record RunnerActivitySnapshot(
    RunnerActivityKind Kind,
    string? OwnerId,
    DateTimeOffset? AcquiredUtc)
{
    public bool IsBusy => Kind != RunnerActivityKind.Idle;
}

public sealed record RunnerActivityConflict(
    RunnerActivityKind Requested,
    RunnerActivityKind Active,
    string ActiveOwnerId,
    DateTimeOffset AcquiredUtc);

public sealed class RunnerActivityCoordinator
{
    public RunnerActivitySnapshot Snapshot();
    public bool TryAcquire(
        RunnerActivityKind kind,
        string ownerId,
        out RunnerActivityLease? lease,
        out RunnerActivityConflict? conflict);
}
```

Use one private lock and a generated lease token. Validate non-Idle kind and nonblank owner ID. `RunnerActivityLease.Dispose()` releases only when its token still matches the coordinator's active token.

- [ ] **Step 4: Run Phase 0 checks**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: activity checks pass; later unimplemented checks may still fail to build.

- [ ] **Step 5: Commit**

```bash
git add src/XemuTestRunner/Authoring/RunnerActivityCoordinator.cs tests/AuthoringChecks
git commit -m "feat: add exclusive authoring activity contracts"
```

### Task 2: Authoring state and readiness contracts

**Files:**
- Create: `src/XemuTestRunner/Authoring/AuthoringSessionContracts.cs`
- Create: `src/XemuTestRunner/Authoring/AuthoringSessionStateMachine.cs`
- Modify: `tests/AuthoringChecks/Program.cs`

**Interfaces:**
- Produces: `AuthoringSessionState`, `AuthoringSessionSnapshot`, `AuthoringReadinessSnapshot`, and `AuthoringSessionStateMachine`.
- `AuthoringSessionStateMachine.Transition(AuthoringSessionState next) -> AuthoringSessionSnapshot`.
- `AuthoringSessionStateMachine.Fail(string code, string detail) -> AuthoringSessionSnapshot`.
- `AuthoringSessionStateMachine.End() -> AuthoringSessionSnapshot`.
- `AuthoringReadinessSnapshot.GetUnmetRequirements() -> IReadOnlyList<string>`.

- [ ] **Step 1: Write failing lifecycle/readiness checks**

Add checks named:

```csharp
await Check("authoring state machine enforces the approved lifecycle", () => { ... });
await Check("authoring failure is terminal and retains its reason", () => { ... });
await Check("authoring readiness requires every hard gate", () => { ... });
```

Test the full happy path through Published/Ended, reject `Created -> Ready`, reject transitions after Failed/Ended, and verify readiness remains false when any one gate is false.

- [ ] **Step 2: Run the checks to verify failure**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: build failure because session contracts do not exist.

- [ ] **Step 3: Implement state contracts**

States:

```csharp
Created, Preflight, LaunchingXemu, StartingCapture,
NegotiatingWebRtc, VerifyingController, Ready, Recording,
Reviewing, ReplayingDraft, Published, Failed, Ended
```

`AuthoringSessionSnapshot` carries session ID, state, state-change UTC, failure code, and failure detail. Normal transitions follow the spec graph. `Fail` is allowed from any nonterminal state; `End` is allowed from any nonterminal state and from Published/Failed. Terminal methods are idempotent only when already in the same terminal state.

Readiness properties:

```csharp
bool XemuProcessAlive
bool QmpReady
bool CaptureTargetOwned
bool CaptureRateQualified
bool EncodeRateQualified
bool BrowserDecoding
bool InputStateChannelOpen
bool SessionControlChannelOpen
bool VirtualGamepadReady
bool FrameProgressing
bool InputLatencyQualified
bool SecureContext
bool StandardGamepadMapped
```

`IsReady` is true only when `GetUnmetRequirements()` is empty. Requirement names are stable lowercase identifiers for API use.

- [ ] **Step 4: Run checks**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: session checks pass.

- [ ] **Step 5: Commit**

```bash
git add src/XemuTestRunner/Authoring/AuthoringSessionContracts.cs src/XemuTestRunner/Authoring/AuthoringSessionStateMachine.cs tests/AuthoringChecks/Program.cs
git commit -m "feat: define authoring lifecycle readiness gates"
```

### Task 3: Complete controller state and frame-aware replay gate

**Files:**
- Create: `src/XemuTestRunner/Authoring/AuthoringInputContracts.cs`
- Modify: `tests/AuthoringChecks/Program.cs`

**Interfaces:**
- Produces: `XboxControllerButtons`, `XboxControllerState`, `BrowserControllerPacket`, `AppliedControllerSample`, `InputHoldRequirement`, and `InputReplayGate`.
- `BrowserControllerPacket.Validate()` validates controller index 0-3 and nonnegative browser timestamp.
- `InputHoldRequirement.Validate()` validates nonnegative minima and positive timeout.
- `InputReplayGate.IsSatisfied(InputHoldRequirement requirement, TimeSpan elapsed, long presentedFrames) -> bool`.

- [ ] **Step 1: Write failing controller/replay checks**

Add checks named:

```csharp
await Check("controller packets preserve complete analog state", () => { ... });
await Check("controller packet validation rejects invalid indices", () => { ... });
await Check("replay gate requires both elapsed time and frames", () => { ... });
```

Use a sample containing simultaneous button flags, trigger extremes, and signed stick values. Assert time-only and frame-only cases are false; exact boundary values are true.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: build failure because controller contracts do not exist.

- [ ] **Step 3: Implement controller/replay contracts**

`XboxControllerState` is a readonly record struct with two byte triggers and four signed 16-bit axes. `Neutral` returns the default state. Button flags include D-pad, Start, Back, stick clicks, shoulders, A/B/X/Y, and Guide.

`AppliedControllerSample` carries sequence, host-applied microseconds, presented frame, controller index, complete state, and the hold requirement. It is a record only; persistence is implemented later.

`InputReplayGate.IsSatisfied` uses logical AND and rejects negative observed frame counts or elapsed time.

- [ ] **Step 4: Run checks**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: controller/replay checks pass.

- [ ] **Step 5: Commit**

```bash
git add src/XemuTestRunner/Authoring/AuthoringInputContracts.cs tests/AuthoringChecks/Program.cs
git commit -m "feat: define complete controller trace contracts"
```

### Task 4: Process-bound window grid and stale-surface contracts

**Files:**
- Create: `src/XemuTestRunner/Authoring/AuthoringWindowContracts.cs`
- Modify: `tests/AuthoringChecks/Program.cs`

**Interfaces:**
- Produces: `WindowGridPoint`, `WindowPixelPoint`, `XemuWindowTargetRole`, `XemuWindowTargetSelector`, `XemuWindowSurface`, `WindowPointerActionKind`, `WindowPointerButton`, `WindowPointerCommand`, and `WindowActionResult`.
- `WindowGridPoint.ToPixel(int width, int height) -> WindowPixelPoint`.
- `WindowPointerCommand.ResolvePixel(XemuWindowSurface surface) -> WindowPixelPoint`.

- [ ] **Step 1: Write failing coordinate and stale-surface checks**

Add checks named:

```csharp
await Check("window grid maps inclusive endpoints and documented example", () => { ... });
await Check("window grid rejects out-of-range coordinates and dimensions", () => { ... });
await Check("pointer commands reject stale or foreign surfaces", () => { ... });
```

Required assertions:

```text
(0,0) on 1920x1080 -> (0,0)
(10000,10000) -> (1919,1079)
(5142,837) -> (987,90)
```

A command must reject a surface whose PID, window ID, or `surfaceVersion` does not match its expected target.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: build failure because window contracts do not exist.

- [ ] **Step 3: Implement window contracts**

Use integer grid values with `Maximum = 10000` and nearest-pixel rounding against `width - 1`/`height - 1`. `XemuWindowSurface` carries process ID, window ID, title, client dimensions, DPI scale, surface version, visibility, minimized, and foreground flags.

`WindowPointerCommand` carries expected process ID, expected window ID, expected surface version, grid point, action, button, hold milliseconds, and wheel delta. Validation must reject arbitrary/blank target identity, invalid hold values, and stale or foreign surfaces before mapping.

- [ ] **Step 4: Run checks**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: window checks pass.

- [ ] **Step 5: Commit**

```bash
git add src/XemuTestRunner/Authoring/AuthoringWindowContracts.cs tests/AuthoringChecks/Program.cs
git commit -m "feat: add verified xemu window coordinate contracts"
```

### Task 5: Platform provider boundaries

**Files:**
- Create: `src/XemuTestRunner/Authoring/AuthoringProviderContracts.cs`
- Modify: `tests/AuthoringChecks/Program.cs`

**Interfaces:**
- Produces: `IXemuGamepadProvider`, `IXemuFrameObserver`, `IXemuCaptureProvider`, `IAuthoringMediaSession`, and `IXemuWindowInputProvider`.
- Consumes: controller/window contracts from Tasks 3-4.

- [ ] **Step 1: Write a contract-shape check**

Add a compile-time check named:

```csharp
await Check("authoring providers require owned process and complete state", () => { ... });
```

Implement minimal fake providers in the test file. The fakes must compile only when:

- gamepad receives complete `XboxControllerState`;
- frame/capture/window providers start against an explicit process ID;
- window input receives a resolved `XemuWindowSurface` and command;
- every provider supports async disposal and cancellation;
- no provider exposes a generic shell command or unrestricted screen coordinate API.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: build failure because provider interfaces do not exist.

- [ ] **Step 3: Implement provider interfaces and snapshots**

Add small immutable records for capture/media/frame health. Do not add concrete platform code or dependencies.

- [ ] **Step 4: Run checks**

Run: `dotnet run --project tests/AuthoringChecks -c Release`

Expected: all provider fakes compile and all checks pass.

- [ ] **Step 5: Commit**

```bash
git add src/XemuTestRunner/Authoring/AuthoringProviderContracts.cs tests/AuthoringChecks/Program.cs
git commit -m "feat: define authoring platform provider boundaries"
```

### Task 6: Add the Phase 0 executable check project to CI

**Files:**
- Create: `tests/AuthoringChecks/AuthoringChecks.csproj`
- Create/complete: `tests/AuthoringChecks/Program.cs`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: all Phase 0 contracts.
- Produces: one cross-platform executable check suite used by CI.

- [ ] **Step 1: Create the console check project**

Use the repository's existing check-project shape:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/XemuTestRunner/XemuTestRunner.csproj" />
  </ItemGroup>
</Project>
```

The program prints `PASS <name>` for each check and returns nonzero when any check fails.

- [ ] **Step 2: Add the CI step**

After RunnerChecks in the Windows/Linux matrix, add:

```yaml
- name: Run test authoring contract checks
  run: dotnet run --project tests/AuthoringChecks -c Release
```

- [ ] **Step 3: Run local verification**

Run:

```bash
dotnet build tests/AuthoringChecks/AuthoringChecks.csproj -c Release --nologo
dotnet run --project tests/AuthoringChecks -c Release --no-build
dotnet build tests/RunnerChecks/RunnerChecks.csproj -c Release --nologo
```

Expected: warning-free builds and all checks pass.

- [ ] **Step 4: Commit**

```bash
git add tests/AuthoringChecks .github/workflows/ci.yml
git commit -m "test: qualify authoring contracts on Windows and Linux"
```

### Task 7: Documentation and Phase 0 review gate

**Files:**
- Modify: `docs/superpowers/specs/2026-09-26-realtime-test-authoring-design.md`
- Modify: `docs/superpowers/plans/2026-09-26-realtime-test-authoring-phase-0.md`
- Create: `docs/TEST-AUTHORING.md`

**Interfaces:**
- Produces: operator/developer entry document linked to issue #65 and the detailed spec/plan.

- [ ] **Step 1: Write the concise feature entry document**

Document:

- distinction between Authoring, `/control`, and automated execution;
- hard no-media-during-testing rule;
- Phase 0 status and intentionally absent runtime routes;
- planned HTTPS/certificate requirements;
- platform qualification boundaries;
- links to issue #65, design, and plan.

- [ ] **Step 2: Run placeholder and contradiction scan**

Run:

```bash
rg -n 'TBD|TODO|FIXME|screenshot fallback|AllowRealtimeStream' \
  docs/TEST-AUTHORING.md \
  docs/superpowers/specs/2026-09-26-realtime-test-authoring-design.md \
  docs/superpowers/plans/2026-09-26-realtime-test-authoring-phase-0.md
```

Expected: no placeholder markers; any textual mention of rejected screenshot fallback or prohibited `AllowRealtimeStream` is intentional explanatory prose.

- [ ] **Step 3: Verify changed-file scope**

Run: `git diff --check main...HEAD`

Expected: no whitespace errors. Confirm no existing runtime path, route, package dependency, or operation policy changed in Phase 0.

- [ ] **Step 4: Commit**

```bash
git add docs/TEST-AUTHORING.md docs/superpowers/specs docs/superpowers/plans
git commit -m "docs: define test authoring delivery boundary"
```

## Plan self-review

- Spec coverage: Phase 0 covers activity ownership, state/readiness, controller state, frame/time replay condition, window grid/stale surfaces, provider boundaries, and cross-platform checks. HTTPS, media, concrete input providers, UI, routes, recording, replay execution, and publication are intentionally assigned to later plans.
- Step scan: each task has an exact file, public signatures, named checks, verification command, and commit boundary.
- Type consistency: controller and window types are produced before provider interfaces consume them; the check project consumes only public contracts.
- Review focus: all five listed failure modes have named checks in their owning tasks.
- Proportion: production bodies are not transcribed; only public signatures, fixed states/fields, and exact assertions are specified.
