using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Control.Gamepad;
using XemuTestRunner.Networking;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

foreach (var (name, button) in new[] {
    ("LStick", XboxControllerButtons.LeftStick), ("RStick", XboxControllerButtons.RightStick),
    ("LeftStick", XboxControllerButtons.LeftStick), ("RightStick", XboxControllerButtons.RightStick) })
    Require(ControllerButtonMap.Resolve(name).Buttons == button, "Incorrect stick alias: " + name);
Console.WriteLine("PASS all UI stick-click aliases resolve");
foreach (var name in new string?[] { null, "", " ", "Guide" })
{
    var rejected = false;
    try { _ = ControllerButtonMap.Resolve(name!); }
    catch (InvalidDataException) { rejected = true; }
    Require(rejected, "Malformed button did not produce a validation error");
}
Console.WriteLine("PASS malformed buttons produce validation errors");

var fake = new FakeProvider();
await using (var session = await ControllerInputSession.StartAsync(fake, CancellationToken.None))
{
    using var control = new XemuControlManager(new XemuControlOptions { InputProvider = "none" });
    using var process = Process.GetCurrentProcess();
    control.Begin(process, Path.GetTempPath(), 0, session);
    try
    {
        foreach (var id in new string?[] { null, "", new('0', 32) })
        {
            var before = session.Transitions.Count;
            var rejected = false;
            try { await control.PressButtonAsync("A", 1, CancellationToken.None, sessionId: id); }
            catch (InvalidOperationException error) when (error.Message.Contains("session changed")) { rejected = true; }
            Require(rejected && session.Transitions.Count == before, "Stale named input reached the device");
        }
        Console.WriteLine("PASS missing and stale manual session IDs submit no input");
        await control.PressButtonAsync("LStick", 1, CancellationToken.None, sessionId: session.SessionId);
        Require(session.Transitions[^2].Receipt.State.Buttons == XboxControllerButtons.LeftStick &&
            session.Transitions[^1].Receipt.State == XboxControllerState.Neutral, "Authorized input or release was lost");
        Console.WriteLine("PASS current-session named input presses and releases the owned device");
        await control.PressButtonAsync("A", 1, CancellationToken.None, record: false);
        Require(session.Transitions[^2].Receipt.State.Buttons == XboxControllerButtons.A,
            "Internal scripted input incorrectly requires a browser session token");
        Console.WriteLine("PASS scripted input retains its non-manual path");

        var gate = (SemaphoreSlim)typeof(XemuControlManager).GetField("_inputGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
        await gate.WaitAsync();
        Task queued;
        try
        {
            queued = control.PressButtonAsync("A", 1, CancellationToken.None, sessionId: session.SessionId);
            control.End();
            control.Begin(process, Path.GetTempPath(), 0, session);
        }
        finally { gate.Release(); }
        var changed = false;
        try { await queued; }
        catch (InvalidOperationException error) when (error.Message.Contains("session changed")) { changed = true; }
        Require(changed, "An input queued before a target replacement entered the new target");
        Console.WriteLine("PASS target replacement is checked inside the input gate");

        // Exercise the actual HTTP body handler with a null session token. It
        // must return validation JSON before needing a live HTTP socket/target.
        var body = "{\"SessionId\":null,\"State\":{},\"DurationMs\":1}";
        var request = new HttpRequest("POST", "/api/v1/input/controller-state", "/api/v1/input/controller-state", "", new Version(1, 1),
            new Dictionary<string, string> { ["Content-Length"] = Encoding.UTF8.GetByteCount(body).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var server = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EmbeddedHttpServer));
        using var transport = new RequestStream("{\"SessionId\":null,\"State\":{},\"DurationMs\":1}");
        var handler = typeof(EmbeddedHttpServer).GetMethod("HandleControllerStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<bool>)handler.Invoke(server, [transport, request, false, CancellationToken.None])!;
        Require(Encoding.UTF8.GetString(transport.Response.ToArray()).StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
            "Null SessionId was not rejected with HTTP 400");
        Console.WriteLine("PASS null-session HTTP request returns 400 instead of throwing");
    }
    finally { control.End(); }
}

sealed class RequestStream(string body) : Stream
{
    private readonly MemoryStream _input = new(Encoding.UTF8.GetBytes(body));
    public MemoryStream Response { get; } = new();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => Response.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => Response.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Response.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { _input.Dispose(); Response.Dispose(); } base.Dispose(disposing); }
}

sealed class FakeProvider : IXemuGamepadProvider
{
    private ulong _sequence;
    public string Name => "repair-fixture-not-os-qualification";
    public bool IsAvailable => true;
    public bool IsReady { get; private set; }
    public void ConfigureTarget(ProcessStartInfo target) { }
    public void ConfigureTarget(IDictionary<string, string> environment) { }
    public Task CreateAsync(int count, CancellationToken cancellationToken) { IsReady = true; return Task.CompletedTask; }
    public Task<NativeGamepadReceipt> ApplyStateAsync(int index, XboxControllerState state, CancellationToken cancellationToken) =>
        Task.FromResult(new NativeGamepadReceipt(++_sequence, Stopwatch.GetTimestamp(), state));
    public Task<NativeGamepadReceipt> NeutralizeAsync(CancellationToken cancellationToken) =>
        ApplyStateAsync(0, XboxControllerState.Neutral, cancellationToken);
    public ValueTask DisposeAsync() { IsReady = false; return ValueTask.CompletedTask; }
}
