using System.Collections.Concurrent;
using System.Diagnostics;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Control.Gamepad;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var checks = 0;
async Task Check(string name, Func<Task> action)
{
    await action();
    checks++;
    Console.WriteLine("PASS " + name);
}

await Check("neutral controller is created and refreshed before target launch", async () =>
{
    var fake = new FakeProvider();
    await using var session = await ControllerInputSession.StartAsync(fake, CancellationToken.None);
    Assert(fake.Created && fake.Applied.First().State == XboxControllerState.Neutral,
        "Controller was not neutral at startup.");
    await Task.Delay(310);
    Assert(fake.Applied.Count >= 5, "Neutral state was not refreshed inside the 250 ms watchdog.");
    Assert(fake.Applied.All(x => x.State == XboxControllerState.Neutral), "Idle session was not neutral.");
    Assert(session.Transitions.Count == 1 && session.RefreshCount >= 4,
        "Idle refreshes were incorrectly recorded as semantic transitions.");
    var launchEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
    session.ConfigureTarget(launchEnvironment);
    Assert(launchEnvironment["TEST_GAMEPAD_READY"] == "1",
        "The actual launch dictionary was not configured after neutral device creation.");
});

await Check("button hold sends one semantic press and returns to neutral", async () =>
{
    var fake = new FakeProvider();
    await using var session = await ControllerInputSession.StartAsync(fake, CancellationToken.None);
    await session.PressButtonAsync(XboxControllerButtons.A, 350, CancellationToken.None);
    var receipts = fake.Applied.ToArray();
    var states = receipts.Select(x => x.State.Buttons).ToArray();
    Assert(states[0] == XboxControllerButtons.None && states.Contains(XboxControllerButtons.A),
        "The button never reached the provider.");
    Assert(states[^1] == XboxControllerButtons.None, "Button remained held after its interval.");
    Assert(states.Count(x => x == XboxControllerButtons.A) >= 4,
        "Held state did not receive watchdog refreshes.");
    Assert(receipts.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, states.Length).Select(x => (ulong)x)),
        "Receipts were not sequenced.");
    Assert(session.Transitions.Count == 3 &&
        session.Transitions[1].Receipt.State.Buttons == XboxControllerButtons.A &&
        session.Transitions[2].Receipt.State == XboxControllerState.Neutral,
        "Applied input and neutral release were not recorded as semantic receipts.");
});

await Check("adjacent full states do not insert a neutral pulse", async () =>
{
    var fake = new FakeProvider();
    await using var session = await ControllerInputSession.StartAsync(fake, CancellationToken.None);
    var first = new XboxControllerState(XboxControllerButtons.A, 0, 255, 16000, 0, 0, 0);
    var second = first with { LeftX = 8000 };
    await session.HoldStateAsync(first, 100, CancellationToken.None);
    await session.HoldStateAsync(second, 100, CancellationToken.None);
    await session.NeutralizeAsync(CancellationToken.None);
    Assert(session.Transitions.Select(x => x.Receipt.State)
        .SequenceEqual(new[] { XboxControllerState.Neutral, first, second, XboxControllerState.Neutral }),
        "A full-state transition introduced a release or lost analog inputs.");
});

await Check("one runner owner and cleanup after cancellation", async () =>
{
    var first = new FakeProvider();
    await using var session = await ControllerInputSession.StartAsync(first, CancellationToken.None);
    try
    {
        await using var _ = await ControllerInputSession.StartAsync(new FakeProvider(), CancellationToken.None);
        throw new InvalidOperationException("Duplicate OS-visible controller owner was accepted.");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("already owned")) { }
    using var cancel = new CancellationTokenSource(150);
    try { await session.PressButtonAsync(XboxControllerButtons.B, 2000, cancel.Token); }
    catch (OperationCanceledException) { }
    Assert(first.Applied.Last().State == XboxControllerState.Neutral,
        "Cancelled hold did not release its button.");
    await session.DisposeAsync();
    Assert(first.Disposed, "Controller was not removed.");
    await using var next = await ControllerInputSession.StartAsync(new FakeProvider(), CancellationToken.None);
});

await Check("refresh failure interrupts a long held action", async () =>
{
    var fake = new FakeProvider { FailOnApplyAt = 5 };
    await using var session = await ControllerInputSession.StartAsync(fake, CancellationToken.None);
    var timer = Stopwatch.StartNew();
    var rejected = false;
    try { await session.HoldStateAsync(new XboxControllerState(XboxControllerButtons.A, 0, 0, 0, 0, 0, 0),
        2000, CancellationToken.None); }
    catch (IOException) { rejected = true; }
    Assert(rejected && timer.ElapsedMilliseconds < 1000,
        "A failed refresh did not abort the held action promptly.");
    Assert(session.FailureTask.IsCompletedSuccessfully &&
        session.FailureTask.Result is IOException,
        "The run supervisor was not notified of the OS submission failure.");
    await session.ReleaseAfterInterruptionAsync();
    Assert(fake.Applied.Last().State == XboxControllerState.Neutral,
        "Interrupted controller state was not neutralized.");
});

await Check("native binding rejects keyboard and ambiguous port configuration", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "controller-binding-" + Guid.NewGuid().ToString("N") + ".toml");
    var sysfs = Path.Combine(Path.GetTempPath(), "controller-sysfs-" + Guid.NewGuid().ToString("N"));
    void Device(string sysname, string name = "Xemu Runner Gamepad", string vendor = "0000",
        string product = "0000", bool joystick = true, bool touchscreen = false)
    {
        var device = Path.Combine(sysfs, sysname);
        Directory.CreateDirectory(Path.Combine(device, "id"));
        Directory.CreateDirectory(Path.Combine(device, "capabilities"));
        File.WriteAllText(Path.Combine(device, "name"), name + "\n");
        File.WriteAllText(Path.Combine(device, "id", "bustype"), "0006\n");
        File.WriteAllText(Path.Combine(device, "id", "vendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(device, "id", "product"), product + "\n");
        File.WriteAllText(Path.Combine(device, "id", "version"), "0001\n");
        File.WriteAllText(Path.Combine(device, "capabilities", "ev"), joystick || touchscreen ? "b\n" : "3\n");
        File.WriteAllText(Path.Combine(device, "capabilities", "abs"), joystick || touchscreen ? "3\n" : "0\n");
        File.WriteAllText(Path.Combine(device, "capabilities", "key"),
            joystick ? "1000000000000 0 0 0 0\n" : touchscreen ? "400 0 0 0 0 0\n" : "0\n");
    }
    try
    {
        Device("input42");
        var valid = "[input]\nauto_bind = false\n[input.virtual_ports]\nport1_connected = 1\n[input.bindings]\nport1 = '0600cc4158656d752052756e6e657200'\nport1_driver = 'usb-xbox-gamepad'\n";
        await File.WriteAllTextAsync(path, valid);
        if (OperatingSystem.IsLinux())
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
        Device("input43");
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("A duplicate virtual controller was accepted.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", name: "Xemu Runner 31363");
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("An SDL GUID collision was accepted.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", name: "Xemu Runner 005408 ");
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("An SDL GUID collision with a trailing space was accepted.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", name: "Xemu Runner Gamepad ");
        if (OperatingSystem.IsLinux())
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", name: "Xemu Runner 222550\n");
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("An SDL GUID collision with an embedded newline was accepted.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", name: "Xemu Runner Gamepad\n");
        if (OperatingSystem.IsLinux())
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43");
        File.WriteAllBytes(Path.Combine(sysfs, "input43", "name"),
            [.. System.Text.Encoding.ASCII.GetBytes("Xemu Runner"), 0xff, 0xed, 0x9f, 0x0a]);
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("An SDL GUID collision with raw non-UTF-8 name bytes was accepted.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", vendor: "1234", product: "5678");
        if (OperatingSystem.IsLinux())
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", joystick: false);
        if (OperatingSystem.IsLinux())
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        Device("input43", joystick: false, touchscreen: true);
        if (OperatingSystem.IsLinux())
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
        Directory.Delete(Path.Combine(sysfs, "input43"), recursive: true);
        await File.WriteAllTextAsync(path, valid.Replace("0600cc4158656d752052756e6e657200", "keyboard"));
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("Keyboard binding was accepted for native input.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
        await File.WriteAllTextAsync(path, valid + "port2 = '0600cc4158656d752052756e6e657200'\n");
        try
        {
            ControllerBindingPreflight.Verify(["-config_path", path], Path.GetDirectoryName(path)!, "input42", sysfs);
            throw new InvalidOperationException("Ambiguous port mapping was accepted.");
        }
        catch (InvalidDataException) when (OperatingSystem.IsLinux()) { }
        catch (NotSupportedException) when (!OperatingSystem.IsLinux()) { }
    }
    finally { File.Delete(path); Directory.Delete(sysfs, recursive: true); }
});

await Check("manual full-state input rejects stale sessions and uses the same pad", async () =>
{
    var fake = new FakeProvider();
    await using var session = await ControllerInputSession.StartAsync(fake, CancellationToken.None);
    using var control = new XemuControlManager(new XemuControlOptions { InputProvider = "none" });
    control.Begin(Process.GetCurrentProcess(), Path.GetTempPath(), 0, session);
    var state = new ControllerStateDefinition { RightTrigger = 255, LeftX = 16000 };
    var before = session.Transitions.Count;
    try
    {
        await control.PressControllerStateAsync(state, 100, "stale-session", CancellationToken.None);
        throw new InvalidOperationException("A stale manual request was accepted.");
    }
    catch (InvalidOperationException error) when (error.Message.Contains("session changed")) { }
    Assert(session.Transitions.Count == before, "Stale request sent controller input.");
    await control.PressControllerStateAsync(state, 100, session.SessionId, CancellationToken.None);
    Assert(session.Transitions[^2].Receipt.State.RightTrigger == 255 &&
        session.Transitions[^1].Receipt.State == XboxControllerState.Neutral,
        "Manual full state did not use the owned controller session.");
    control.End();
});

await Check("failed helper removal quarantines native ownership", async () =>
{
    var session = await ControllerInputSession.StartAsync(
        new FakeProvider { FailDispose = true }, CancellationToken.None);
    try { await session.DisposeAsync(); throw new InvalidOperationException("Failed removal was accepted."); }
    catch (IOException) { }
    try
    {
        await using var _ = await ControllerInputSession.StartAsync(new FakeProvider(), CancellationToken.None);
        throw new InvalidOperationException("A second OS-visible controller was created after unconfirmed removal.");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("already owned")) { }
});

Console.WriteLine($"Controller integration checks: {checks}/{checks} passed.");

sealed class FakeProvider : IXemuGamepadProvider
{
    private ulong _sequence;
    public ConcurrentQueue<NativeGamepadReceipt> Applied { get; } = new();
    public string Name => "fake-os-pad";
    public bool IsAvailable => true;
    public bool IsReady => Created && !Disposed;
    public bool Created { get; private set; }
    public bool Disposed { get; private set; }
    public int? FailOnApplyAt { get; init; }
    public bool FailDispose { get; init; }
    private bool _failedOnce;
    public void ConfigureTarget(ProcessStartInfo target) { }
    public void ConfigureTarget(IDictionary<string, string> launchEnvironment)
    {
        if (!Created || Applied.IsEmpty)
            throw new InvalidOperationException("Target configured before neutral device creation.");
        launchEnvironment["TEST_GAMEPAD_READY"] = "1";
    }
    public Task CreateAsync(int controllerCount, CancellationToken cancellationToken)
    {
        if (controllerCount != 1) throw new InvalidOperationException("Unexpected controller count.");
        Created = true;
        return Task.CompletedTask;
    }
    public Task<NativeGamepadReceipt> ApplyStateAsync(int controllerIndex, XboxControllerState state, CancellationToken cancellationToken)
    {
        if (!IsReady || controllerIndex != 0) throw new InvalidOperationException("Input sent to an unavailable pad.");
        if (!_failedOnce && FailOnApplyAt == (int)_sequence + 1)
        {
            _failedOnce = true;
            throw new IOException("Fixture OS submission failed.");
        }
        var receipt = new NativeGamepadReceipt(++_sequence, Stopwatch.GetTimestamp(), state);
        Applied.Enqueue(receipt);
        return Task.FromResult(receipt);
    }
    public Task<NativeGamepadReceipt> NeutralizeAsync(CancellationToken cancellationToken) =>
        ApplyStateAsync(0, XboxControllerState.Neutral, cancellationToken);
    public ValueTask DisposeAsync()
    {
        if (FailDispose) throw new IOException("Fixture helper removal was not confirmed.");
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
