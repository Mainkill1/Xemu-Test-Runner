using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using XemuTestRunner.Authoring;

// This child tests the managed pipe/lifetime contract, NOT OS gamepad injection.
// The separate native workflow must qualify actual OS state through SDL/XInput.
if (args.Length == 2 && args[0] == "--pipe-fixture")
{
    string mode = args[1];
    if (mode == "startup-error") { Console.Error.WriteLine("fixture startup denied"); return 3; }
    if (mode == "startup-hang") { await Task.Delay(30_000); return 3; }
    if (mode == "stderr-flood") Console.Error.Write(new string('e', 100_000));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "ready", protocolVersion = 1,
        backend = OperatingSystem.IsWindows() ? "windows-input-injector" : "linux-uinput",
        supportedButtons = 0xf3ff, watchdogMs = 250, systemWide = true,
        additionalDriver = mode == "wrong-provider", deviceSysname = "input-fixture"
    }));
    while (Console.ReadLine() is { } line)
    {
        if (line == "stop")
        {
            if (mode == "slow-stop") await Task.Delay(150);
            return 0;
        }
        if (mode == "reply-hang") { await Task.Delay(30_000); return 3; }
        if (mode == "oversized") { Console.WriteLine(new string('x', 5000)); continue; }
        string[] fields = line.Split(' ');
        if (fields.Length != 9 || fields[0] != "state") return 4;
        long sequence = long.Parse(fields[1], CultureInfo.InvariantCulture);
        foreach (string field in fields.Skip(2)) _ = long.Parse(field, CultureInfo.InvariantCulture);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "applied", sequence = mode == "wrong-sequence" ? sequence + 1 : sequence,
            appliedAtUs = mode == "backward-clock" ? 100 - sequence : (long)(Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000.0))
        }));
    }
    return 0;
}

static NativeGamepadProvider Provider(string mode, TimeSpan? timeout = null)
{
    string path = Environment.ProcessPath!;
    var arguments = new List<string>();
    if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        arguments.Add(Assembly.GetExecutingAssembly().Location);
    arguments.AddRange(["--pipe-fixture", mode]);
    return new NativeGamepadProvider(path, arguments, timeout ?? TimeSpan.FromSeconds(3));
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Throws<T>(Func<Task> work) where T : Exception
{
    try { await work(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static void Exited(int? id)
{
    Assert(id.HasValue, "Worker process was never recorded.");
    try { using var p = Process.GetProcessById(id!.Value); Assert(p.HasExited, "Worker leaked after cleanup."); }
    catch (ArgumentException) { }
}

int failures = 0, count = 0;
async Task Check(string name, Func<Task> work)
{
    count++;
    try { await work(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}

await Check("full-state receipt and per-process target configuration", async () =>
{
    await using var provider = Provider("good");
    await provider.CreateAsync(1, CancellationToken.None);
    Assert(provider.IsReady, "Provider did not validate ready.");
    var target = new ProcessStartInfo("xemu");
    string? global = Environment.GetEnvironmentVariable("SDL_JOYSTICK_RAWINPUT");
    provider.ConfigureTarget(target);
    if (OperatingSystem.IsWindows()) Assert(target.Environment["SDL_JOYSTICK_RAWINPUT"] == "0", "Missing XInput selection.");
    else Assert(target.Environment["SDL_GAMECONTROLLERCONFIG"]!.Contains("Xemu Runner Gamepad,"), "Missing native mapping.");
    Assert(Environment.GetEnvironmentVariable("SDL_JOYSTICK_RAWINPUT") == global, "Changed global environment.");
    var state = new XboxControllerState(XboxControllerButtons.A | XboxControllerButtons.B,
        23, 254, short.MinValue, short.MaxValue, 12345, -5432);
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    await provider.ApplyStateAsync(0, state, CancellationToken.None);
    var receipt = provider.LastApplied;
    Assert(receipt is { Sequence: 1, AppliedAtUs: >= 0 } && receipt.State == state, "Receipt did not retain full state.");
    await provider.NeutralizeAsync(CancellationToken.None);
    Assert(provider.LastApplied is { Sequence: 2 } && provider.LastApplied.State == default, "Neutralization not acknowledged.");
    int? pid = provider.WorkerProcessId;
    await provider.DisposeAsync(); await provider.DisposeAsync();
    Assert(!provider.IsReady, "Disposed provider remained ready."); Exited(pid);
});
await Check("unsupported count and invalid input never reach the native helper", async () =>
{
    await using var provider = Provider("good");
    await Throws<NotSupportedException>(() => provider.CreateAsync(2, CancellationToken.None));
    Assert(provider.WorkerProcessId is null, "Unsupported count launched a device.");
    await provider.CreateAsync(1, CancellationToken.None);
    await Throws<ArgumentOutOfRangeException>(() => provider.ApplyStateAsync(1, default, CancellationToken.None));
    await Throws<InvalidDataException>(() => provider.ApplyStateAsync(0,
        new XboxControllerState((XboxControllerButtons)0x800, 0, 0, 0, 0, 0, 0), CancellationToken.None));
    Assert(provider.LastApplied is null, "Invalid state produced a receipt.");
});
await Check("startup denial retains native stderr and exits", async () =>
{
    await using var provider = Provider("startup-error");
    try { await provider.CreateAsync(1, CancellationToken.None); throw new InvalidOperationException("Accepted failed startup."); }
    catch (IOException e) { Assert(e.Message.Contains("fixture startup denied"), "Lost native startup reason."); }
    Exited(provider.WorkerProcessId);
});
foreach (string mode in new[] { "wrong-provider", "oversized", "wrong-sequence" })
    await Check("refuse " + mode, async () =>
    {
        await using var provider = Provider(mode);
        if (mode == "wrong-provider") await Throws<IOException>(() => provider.CreateAsync(1, CancellationToken.None));
        else
        {
            await provider.CreateAsync(1, CancellationToken.None);
            await Throws<IOException>(() => provider.ApplyStateAsync(0, default, CancellationToken.None));
        }
        Assert(!provider.IsReady && provider.LastApplied is null, "Protocol failure was accepted.");
        Exited(provider.WorkerProcessId);
    });
await Check("cancelled startup kills the child", async () =>
{
    await using var provider = Provider("startup-hang");
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    await Throws<OperationCanceledException>(() => provider.CreateAsync(1, cancel.Token));
    Exited(provider.WorkerProcessId);
});
await Check("bounded receipt timeout kills the child", async () =>
{
    await using var provider = Provider("reply-hang", TimeSpan.FromSeconds(2));
    await provider.CreateAsync(1, CancellationToken.None);
    await Throws<TimeoutException>(() => provider.ApplyStateAsync(0, default, CancellationToken.None));
    Assert(provider.LastApplied is null, "Timed-out input was acknowledged."); Exited(provider.WorkerProcessId);
});
await Check("stderr is drained without blocking device readiness", async () =>
{
    await using var provider = Provider("stderr-flood");
    await provider.CreateAsync(1, CancellationToken.None);
    await provider.ApplyStateAsync(0, default, CancellationToken.None);
    Assert(provider.LastApplied is not null, "Stderr blocked input submission.");
});
await Check("receipt clock cannot regress", async () =>
{
    await using var provider = Provider("backward-clock");
    await provider.CreateAsync(1, CancellationToken.None);
    await provider.ApplyStateAsync(0, default, CancellationToken.None);
    await Throws<IOException>(() => provider.ApplyStateAsync(0, default, CancellationToken.None));
    Assert(provider.LastApplied is { Sequence: 1 }, "Rejected state replaced last valid receipt.");
    Exited(provider.WorkerProcessId);
});
await Check("concurrent disposal waits for actual exit", async () =>
{
    var provider = Provider("slow-stop");
    await provider.CreateAsync(1, CancellationToken.None);
    int? pid = provider.WorkerProcessId;
    Task first = provider.DisposeAsync().AsTask();
    await provider.DisposeAsync();
    Assert(first.IsCompleted, "Second disposal returned before the first cleanup.");
    Exited(pid);
});
Console.WriteLine($"Managed gamepad checks: {count - failures}/{count} passed (pipe fixtures, not OS qualification).");
return failures == 0 ? 0 : 1;
