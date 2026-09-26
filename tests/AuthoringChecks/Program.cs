using System.Diagnostics.CodeAnalysis;
using XemuTestRunner.Authoring;

var failures = 0;

async Task Check(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

static void Assert(
    [DoesNotReturnIf(false)] bool condition,
    string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

await Check("execution and authoring leases are mutually exclusive", () =>
{
    var coordinator = new RunnerActivityCoordinator();
    Assert(!coordinator.Snapshot().IsBusy, "A new coordinator was not idle.");

    Assert(coordinator.TryAcquire(
            RunnerActivityKind.TestExecution,
            "run-001",
            out var execution,
            out var unexpectedConflict) &&
        execution is not null,
        "The execution lease was rejected: " + unexpectedConflict);

    Assert(!coordinator.TryAcquire(
            RunnerActivityKind.TestAuthoring,
            "authoring-001",
            out var blockedLease,
            out var conflict),
        "Authoring acquired the runner while execution owned it.");
    Assert(blockedLease is null, "A rejected acquisition returned a live lease.");
    Assert(conflict is not null, "A rejected acquisition did not explain the active owner.");
    Assert(conflict.Requested == RunnerActivityKind.TestAuthoring,
        "The conflict did not retain the requested activity.");
    Assert(conflict.Active == RunnerActivityKind.TestExecution,
        "The conflict did not retain the active activity.");
    Assert(conflict.ActiveOwnerId == "run-001",
        "The conflict did not retain the execution owner ID.");

    execution.Dispose();
    Assert(!coordinator.Snapshot().IsBusy, "Disposing the execution lease did not release ownership.");

    Assert(coordinator.TryAcquire(
            RunnerActivityKind.TestAuthoring,
            "authoring-001",
            out var authoring,
            out var authoringConflict) &&
        authoring is not null,
        "The authoring lease was rejected after execution released it: " + authoringConflict);

    authoring.Dispose();
    AssertThrows<ArgumentOutOfRangeException>(
        () => coordinator.TryAcquire(
            RunnerActivityKind.Idle,
            "invalid-owner",
            out _,
            out _),
        "Idle was accepted as an owned activity.");

    return Task.CompletedTask;
});

await Check("disposing a stale lease cannot release a newer owner", () =>
{
    var coordinator = new RunnerActivityCoordinator();
    Assert(coordinator.TryAcquire(
            RunnerActivityKind.TestAuthoring,
            "authoring-old",
            out var oldLease,
            out _) &&
        oldLease is not null,
        "Could not acquire the initial authoring lease.");

    oldLease.Dispose();

    Assert(coordinator.TryAcquire(
            RunnerActivityKind.TestExecution,
            "run-new",
            out var newLease,
            out _) &&
        newLease is not null,
        "Could not acquire the replacement execution lease.");

    oldLease.Dispose();
    var snapshot = coordinator.Snapshot();
    Assert(snapshot.Kind == RunnerActivityKind.TestExecution,
        "A stale lease released the newer execution owner.");
    Assert(snapshot.OwnerId == "run-new", "A stale lease changed the newer owner ID.");

    newLease.Dispose();
    return Task.CompletedTask;
});

await Check("activity snapshots retain owner and acquisition time", () =>
{
    var coordinator = new RunnerActivityCoordinator();
    var before = DateTimeOffset.UtcNow;
    Assert(coordinator.TryAcquire(
            RunnerActivityKind.TestAuthoring,
            "authoring-snapshot",
            out var lease,
            out _) &&
        lease is not null,
        "Could not acquire the authoring lease.");

    var snapshot = coordinator.Snapshot();
    Assert(snapshot.Kind == RunnerActivityKind.TestAuthoring, "Snapshot lost the activity kind.");
    Assert(snapshot.OwnerId == "authoring-snapshot", "Snapshot lost the owner ID.");
    Assert(snapshot.AcquiredUtc is not null && snapshot.AcquiredUtc >= before,
        "Snapshot did not retain a valid acquisition time.");

    lease.Dispose();
    return Task.CompletedTask;
});

await Check("authoring state machine enforces the approved lifecycle", () =>
{
    var machine = new AuthoringSessionStateMachine("authoring-state");
    var expected = new[]
    {
        AuthoringSessionState.Preflight,
        AuthoringSessionState.LaunchingXemu,
        AuthoringSessionState.StartingCapture,
        AuthoringSessionState.NegotiatingWebRtc,
        AuthoringSessionState.VerifyingController,
        AuthoringSessionState.Ready,
        AuthoringSessionState.Recording,
        AuthoringSessionState.Reviewing,
        AuthoringSessionState.ReplayingDraft,
        AuthoringSessionState.Published
    };

    foreach (var state in expected)
    {
        var snapshot = machine.Transition(state);
        Assert(snapshot.State == state, $"The state machine did not enter {state}.");
    }

    var ended = machine.End();
    Assert(ended.State == AuthoringSessionState.Ended, "Published session did not end.");
    AssertThrows<InvalidOperationException>(
        () => machine.Transition(AuthoringSessionState.Ready),
        "A terminal session accepted another transition.");

    var skipped = new AuthoringSessionStateMachine("authoring-skip");
    AssertThrows<InvalidOperationException>(
        () => skipped.Transition(AuthoringSessionState.Ready),
        "The session skipped directly from Created to Ready.");

    return Task.CompletedTask;
});

await Check("authoring failure is terminal and retains its reason", () =>
{
    var machine = new AuthoringSessionStateMachine("authoring-failure");
    machine.Transition(AuthoringSessionState.Preflight);
    var failed = machine.Fail("authoring_media_unavailable", "Encoder missed the readiness floor.");

    Assert(failed.State == AuthoringSessionState.Failed, "Failure did not set the Failed state.");
    Assert(failed.FailureCode == "authoring_media_unavailable", "Failure code was not retained.");
    Assert(failed.FailureDetail == "Encoder missed the readiness floor.",
        "Failure detail was not retained.");
    AssertThrows<InvalidOperationException>(
        () => machine.Transition(AuthoringSessionState.LaunchingXemu),
        "A failed session accepted a normal transition.");

    return Task.CompletedTask;
});

await Check("authoring readiness requires every hard gate", () =>
{
    var ready = new AuthoringReadinessSnapshot(
        XemuProcessAlive: true,
        QmpReady: true,
        CaptureTargetOwned: true,
        CaptureRateQualified: true,
        EncodeRateQualified: true,
        BrowserDecoding: true,
        InputStateChannelOpen: true,
        SessionControlChannelOpen: true,
        VirtualGamepadReady: true,
        FrameProgressing: true,
        InputLatencyQualified: true,
        SecureContext: true,
        StandardGamepadMapped: true);

    Assert(ready.IsReady, "A fully qualified session was not ready.");
    Assert(ready.GetUnmetRequirements().Count == 0,
        "A fully qualified session reported unmet requirements.");

    var missingFrameProgress = ready with { FrameProgressing = false };
    Assert(!missingFrameProgress.IsReady,
        "A session without frame progress was considered ready.");
    Assert(missingFrameProgress.GetUnmetRequirements().SequenceEqual(["frame_progressing"]),
        "Readiness did not report the missing frame-progress gate.");

    var missingSeveral = ready with
    {
        SecureContext = false,
        VirtualGamepadReady = false,
        BrowserDecoding = false
    };
    var unmet = missingSeveral.GetUnmetRequirements();
    Assert(unmet.Contains("secure_context", StringComparer.Ordinal),
        "Readiness omitted the secure-context failure.");
    Assert(unmet.Contains("virtual_gamepad_ready", StringComparer.Ordinal),
        "Readiness omitted the virtual-gamepad failure.");
    Assert(unmet.Contains("browser_decoding", StringComparer.Ordinal),
        "Readiness omitted the browser-decode failure.");

    return Task.CompletedTask;
});

await Check("controller packets preserve complete analog state", () =>
{
    var state = new XboxControllerState(
        XboxControllerButtons.DPadUp |
        XboxControllerButtons.A |
        XboxControllerButtons.RightShoulder,
        LeftTrigger: 0,
        RightTrigger: byte.MaxValue,
        LeftX: 8140,
        LeftY: short.MinValue,
        RightX: short.MaxValue,
        RightY: -1234);
    var packet = new BrowserControllerPacket(
        Sequence: 184,
        BrowserTimestampUs: 7_912_110,
        ControllerIndex: 0,
        State: state);

    packet.Validate();
    Assert(packet.State.Buttons.HasFlag(XboxControllerButtons.A), "A button flag was lost.");
    Assert(packet.State.RightTrigger == byte.MaxValue, "Right trigger value was lost.");
    Assert(packet.State.LeftY == short.MinValue, "Left stick Y value was lost.");
    Assert(packet.State.RightX == short.MaxValue, "Right stick X value was lost.");
    Assert(XboxControllerState.Neutral == default, "Neutral controller state is not the default state.");

    return Task.CompletedTask;
});

await Check("controller packet validation rejects invalid indices", () =>
{
    var packet = new BrowserControllerPacket(1, 0, 4, XboxControllerState.Neutral);
    AssertThrows<InvalidDataException>(
        packet.Validate,
        "A fifth controller index was accepted.");

    var negativeTimestamp = packet with { ControllerIndex = 0, BrowserTimestampUs = -1 };
    AssertThrows<InvalidDataException>(
        negativeTimestamp.Validate,
        "A negative browser timestamp was accepted.");

    return Task.CompletedTask;
});

await Check("replay gate requires both elapsed time and frames", () =>
{
    var requirement = new InputHoldRequirement(
        MinimumElapsedMs: 250,
        MinimumPresentedFrames: 15,
        TimeoutMs: 5000);
    requirement.Validate();

    Assert(!InputReplayGate.IsSatisfied(requirement, TimeSpan.FromMilliseconds(249), 15),
        "Frame progress alone satisfied the replay gate.");
    Assert(!InputReplayGate.IsSatisfied(requirement, TimeSpan.FromMilliseconds(250), 14),
        "Elapsed time alone satisfied the replay gate.");
    Assert(InputReplayGate.IsSatisfied(requirement, TimeSpan.FromMilliseconds(250), 15),
        "Exact time/frame boundaries did not satisfy the replay gate.");
    Assert(InputReplayGate.IsSatisfied(requirement, TimeSpan.FromSeconds(1), 60),
        "Values above both minima did not satisfy the replay gate.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => InputReplayGate.IsSatisfied(requirement, TimeSpan.FromMilliseconds(-1), 15),
        "Negative elapsed time was accepted.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => InputReplayGate.IsSatisfied(requirement, TimeSpan.FromMilliseconds(250), -1),
        "Negative frame progress was accepted.");

    return Task.CompletedTask;
});

await Check("window grid maps inclusive endpoints and documented example", () =>
{
    Assert(new WindowGridPoint(0, 0).ToPixel(1920, 1080) == new WindowPixelPoint(0, 0),
        "The grid origin did not map to the first pixel.");
    Assert(new WindowGridPoint(10_000, 10_000).ToPixel(1920, 1080) ==
           new WindowPixelPoint(1919, 1079),
        "The grid maximum did not map to the last pixel.");
    Assert(new WindowGridPoint(5142, 837).ToPixel(1920, 1080) ==
           new WindowPixelPoint(987, 90),
        "The documented grid example changed.");

    return Task.CompletedTask;
});

await Check("window grid rejects out-of-range coordinates and dimensions", () =>
{
    AssertThrows<InvalidDataException>(
        () => new WindowGridPoint(-1, 0).ToPixel(1920, 1080),
        "A negative X grid coordinate was accepted.");
    AssertThrows<InvalidDataException>(
        () => new WindowGridPoint(0, 10_001).ToPixel(1920, 1080),
        "A Y grid coordinate above 10000 was accepted.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => new WindowGridPoint(0, 0).ToPixel(0, 1080),
        "A zero-width surface was accepted.");

    return Task.CompletedTask;
});

await Check("pointer commands reject stale or foreign surfaces", () =>
{
    var surface = new XemuWindowSurface(
        ProcessId: 42,
        WindowId: "window-1",
        Title: "xemu",
        ClientWidth: 1920,
        ClientHeight: 1080,
        DpiScale: 1,
        SurfaceVersion: 12,
        IsVisible: true,
        IsMinimized: false,
        IsForeground: true);
    var command = new WindowPointerCommand(
        ExpectedProcessId: 42,
        ExpectedWindowId: "window-1",
        ExpectedSurfaceVersion: 12,
        Point: new WindowGridPoint(5142, 837),
        Action: WindowPointerActionKind.Click,
        Button: WindowPointerButton.Left,
        HoldMs: 80,
        WheelDelta: 0);

    Assert(command.ResolvePixel(surface) == new WindowPixelPoint(987, 90),
        "A current owned surface did not resolve the pointer coordinate.");
    AssertThrows<InvalidOperationException>(
        () => command.ResolvePixel(surface with { SurfaceVersion = 13 }),
        "A stale surface version was accepted.");
    AssertThrows<InvalidOperationException>(
        () => command.ResolvePixel(surface with { ProcessId = 43 }),
        "A foreign process surface was accepted.");
    AssertThrows<InvalidOperationException>(
        () => command.ResolvePixel(surface with { WindowId = "window-2" }),
        "A different window ID was accepted.");

    return Task.CompletedTask;
});

await Check("authoring providers require owned process and complete state", async () =>
{
    await using IXemuGamepadProvider gamepad = new FakeGamepadProvider();
    await gamepad.CreateAsync(1, CancellationToken.None);
    await gamepad.ApplyStateAsync(0, XboxControllerState.Neutral, CancellationToken.None);
    await gamepad.NeutralizeAsync(CancellationToken.None);

    await using IXemuFrameObserver frameObserver = new FakeFrameObserver();
    await frameObserver.StartAsync(42, CancellationToken.None);
    var frame = await frameObserver.GetSnapshotAsync(CancellationToken.None);
    Assert(frame.ProcessId == 42, "Frame observer did not remain bound to the owned process.");

    await using IXemuCaptureProvider capture = new FakeCaptureProvider();
    await capture.StartAsync(42, CancellationToken.None);
    var captureHealth = await capture.GetHealthAsync(CancellationToken.None);
    Assert(captureHealth.ProcessId == 42, "Capture provider did not remain bound to the owned process.");

    await using IAuthoringMediaSession media = new FakeMediaSession();
    await media.StartAsync(
        new AuthoringMediaProfile(1280, 720, 60, 55, "H264"),
        CancellationToken.None);
    Assert((await media.GetHealthAsync(CancellationToken.None)).Qualified,
        "Media provider did not report its qualification state.");
    await media.StopAsync(CancellationToken.None);

    await using IXemuWindowInputProvider windowInput = new FakeWindowInputProvider();
    var surfaces = await windowInput.EnumerateOwnedSurfacesAsync(42, CancellationToken.None);
    var surface = surfaces.Single();
    var command = new WindowPointerCommand(
        42,
        surface.WindowId,
        surface.SurfaceVersion,
        new WindowGridPoint(5000, 5000),
        WindowPointerActionKind.Move,
        WindowPointerButton.None,
        0,
        0);
    var result = await windowInput.ApplyPointerAsync(surface, command, CancellationToken.None);
    Assert(result.ProcessId == 42, "Window provider returned a foreign process result.");
    await windowInput.ApplyKeyChordAsync(surface, ["CTRL", "R"], 100, CancellationToken.None);
    await windowInput.NeutralizeAsync(CancellationToken.None);
});

return failures == 0 ? 0 : 1;

sealed class FakeGamepadProvider : IXemuGamepadProvider
{
    public string Name => "fake-gamepad";
    public bool IsAvailable => true;

    public Task CreateAsync(int controllerCount, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ApplyStateAsync(
        int controllerIndex,
        XboxControllerState state,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task NeutralizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeFrameObserver : IXemuFrameObserver
{
    private int _processId;
    public string Name => "fake-frame-observer";
    public bool IsAvailable => true;

    public Task StartAsync(int processId, CancellationToken cancellationToken)
    {
        _processId = processId;
        return Task.CompletedTask;
    }

    public ValueTask<FrameProgressSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FrameProgressSnapshot(_processId, 15, 1_000_000, 1));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeCaptureProvider : IXemuCaptureProvider
{
    private int _processId;
    public string Name => "fake-capture";
    public bool IsAvailable => true;

    public Task StartAsync(int processId, CancellationToken cancellationToken)
    {
        _processId = processId;
        return Task.CompletedTask;
    }

    public ValueTask<CaptureHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CaptureHealthSnapshot(
            _processId,
            SurfaceVersion: 1,
            Width: 1280,
            Height: 720,
            CapturedFrames: 60,
            CaptureFramesPerSecond: 60,
            LastFrameTimestampUs: 1_000_000,
            Qualified: true));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeMediaSession : IAuthoringMediaSession
{
    public string Name => "fake-media";
    public bool IsAvailable => true;

    public Task StartAsync(AuthoringMediaProfile profile, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask<AuthoringMediaHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AuthoringMediaHealthSnapshot(
            EncodeFramesPerSecond: 60,
            DecodeFramesPerSecond: 60,
            EncodedFrames: 60,
            DecodedFrames: 60,
            RoundTripMilliseconds: 30,
            PacketLossPercent: 0,
            Qualified: true));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeWindowInputProvider : IXemuWindowInputProvider
{
    public string Name => "fake-window-input";
    public bool IsAvailable => true;

    public Task<IReadOnlyList<XemuWindowSurface>> EnumerateOwnedSurfacesAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<XemuWindowSurface> result =
        [
            new XemuWindowSurface(
                processId,
                "window-1",
                "xemu",
                1280,
                720,
                1,
                1,
                true,
                false,
                true)
        ];
        return Task.FromResult(result);
    }

    public Task<WindowActionResult> ApplyPointerAsync(
        XemuWindowSurface surface,
        WindowPointerCommand command,
        CancellationToken cancellationToken)
    {
        var pixel = command.ResolvePixel(surface);
        return Task.FromResult(new WindowActionResult(
            surface.ProcessId,
            surface.WindowId,
            surface.SurfaceVersion,
            command.Point,
            pixel));
    }

    public Task ApplyKeyChordAsync(
        XemuWindowSurface surface,
        IReadOnlyList<string> keys,
        int holdMs,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task NeutralizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
