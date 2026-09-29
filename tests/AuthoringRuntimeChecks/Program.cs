using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XemuTestRunner.Authoring;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;

var failures = 0;
var count = 0;
async Task Check(string name, Func<Task> test)
{
    count++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
static void Assert([DoesNotReturnIf(false)] bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
var root = Path.Combine(Path.GetTempPath(), "authoring-runtime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await Check("the actual queue refuses claims during authoring", () =>
    {
        var workspace = Path.Combine(root, "queue");
        var paths = new RunnerPaths("unused", workspace, Path.Combine(workspace, "pending"),
            Path.Combine(workspace, "testing"), Path.Combine(workspace, "tested"),
            Path.Combine(workspace, "results"), Path.Combine(workspace, "files"));
        var queue = new JobQueue(new RunnerConfig(), paths);
        queue.EnsureDirectories();
        var lease = queue.BeginAuthoring("author-a");
        Assert(queue.TryClaimNext().Issue?.Code == "authoring_active", "Queue did not enforce authoring ownership.");
        Throws<InvalidOperationException>(() => queue.BeginAuthoring("author-b"));
        queue.EndAuthoring(lease);
        Assert(queue.TryClaimNext().Issue is null, "Queue stayed blocked after confirmed release.");
        Directory.CreateDirectory(Path.Combine(paths.Testing, "preserved"));
        Throws<InvalidOperationException>(() => queue.BeginAuthoring("author-c"));
        return Task.CompletedTask;
    });

    await Check("restart does not forget an unresolved authoring owner", () =>
    {
        var workspace = Path.Combine(root, "restart");
        var paths = new RunnerPaths("unused", workspace, Path.Combine(workspace, "pending"),
            Path.Combine(workspace, "testing"), Path.Combine(workspace, "tested"),
            Path.Combine(workspace, "results"), Path.Combine(workspace, "files"));
        var original = new JobQueue(new RunnerConfig(), paths);
        original.EnsureDirectories();
        var token = original.BeginAuthoring("author-interrupted");
        var restarted = new JobQueue(new RunnerConfig(), paths);
        Assert(restarted.TryClaimNext().Issue?.Code == "authoring_recovery_required", "Unresolved ownership was silently released.");
        Throws<InvalidOperationException>(() => restarted.BeginAuthoring("replacement"));
        Throws<InvalidOperationException>(() => original.EndAuthoring(Guid.NewGuid()));
        original.EndAuthoring(token);
        return Task.CompletedTask;
    });

    await Check("draft revisions are durable and reject lost updates", () =>
    {
        var storePath = Path.Combine(root, "drafts");
        var store = new AuthoringDraftStore(storePath);
        var draft = store.Create("draft-one", "Test one", "seed", new string('a', 64), "build-one");
        var updated = store.Replace("draft-one", draft.Revision, draft with { Name = "Renamed" });
        Assert(updated.Revision != draft.Revision, "Editing did not advance revision.");
        Throws<InvalidOperationException>(() => store.Replace("draft-one", draft.Revision, draft));
        var reopened = new AuthoringDraftStore(storePath).Read("draft-one");
        Assert(reopened.Name == "Renamed" && reopened.Revision == updated.Revision, "Draft did not survive reopening.");
        Throws<InvalidDataException>(() => store.Read("../escape"));
        Throws<InvalidDataException>(() => store.Create("bad", "bad", "seed", "short", "build"));
        return Task.CompletedTask;
    });

    await Check("recording retains applied state and rejects stale packets", async () =>
    {
        await using var provider = new RecordingGamepad();
        var recording = new AppliedInputRecorder(provider, TimeProvider.System, maximumSamples: 10);
        var state = new XboxControllerState(XboxControllerButtons.A | XboxControllerButtons.B,
            12, 254, short.MinValue, 8123, short.MaxValue, -998);
        Assert(await recording.ApplyAsync(new BrowserControllerPacket(1, 0, 0, state), CancellationToken.None), "First state was rejected.");
        Assert(!await recording.ApplyAsync(new BrowserControllerPacket(1, 0, 0, XboxControllerState.Neutral), CancellationToken.None), "Duplicate overwrote state.");
        var samples = recording.Snapshot();
        Assert(samples.Count == 1 && samples[0].State == state, "Applied analog/chord state was not recorded.");
        Assert(provider.Last == state && provider.Applied == 1, "A stale packet reached the native provider.");
        Assert(samples[0].AppliedAtUs >= 0, "Recorded clock was not monotonic-relative.");
    });

    await Check("provider rejection cannot become an applied input receipt", async () =>
    {
        await using var provider = new RecordingGamepad { Reject = true };
        var recording = new AppliedInputRecorder(provider, TimeProvider.System, maximumSamples: 10);
        try { await recording.ApplyAsync(new BrowserControllerPacket(1, 0, 0, XboxControllerState.Neutral), CancellationToken.None); }
        catch (IOException) { }
        Assert(recording.Snapshot().Count == 0, "An unapplied input was retained as applied.");
    });

    await Check("pointer resolution rejects unusable surfaces", () =>
    {
        var command = new WindowPointerCommand(42, "window", 1, new WindowGridPoint(5000, 5000),
            WindowPointerActionKind.Click, WindowPointerButton.Left, 80, 0);
        var surface = new XemuWindowSurface(42, "window", "xemu", 800, 600, 1, 1, true, false, true);
        Throws<InvalidOperationException>(() => command.ResolvePixel(surface with { IsForeground = false }));
        Throws<InvalidOperationException>(() => command.ResolvePixel(surface with { IsVisible = false }));
        Throws<InvalidOperationException>(() => command.ResolvePixel(surface with { IsMinimized = true }));
        Throws<InvalidDataException>(() => (command with { Action = (WindowPointerActionKind)99 }).Validate());
        Throws<InvalidDataException>(() => (command with { Point = new WindowGridPoint(-1, 0) }).Validate());
        return Task.CompletedTask;
    });

    await Check("input hold contracts reject impossible deadlines", () =>
    {
        Throws<InvalidDataException>(() => new InputHoldRequirement(500, 1, 499).Validate());
        Throws<ArgumentOutOfRangeException>(() => new RunnerActivityCoordinator().TryAcquire((RunnerActivityKind)99, "x", out _, out _));
        return Task.CompletedTask;
    });
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Authoring runtime checks: {count - failures}/{count} passed.");
return failures == 0 ? 0 : 1;

sealed class RecordingGamepad : IXemuGamepadProvider
{
    public string Name => "test-only";
    public bool IsAvailable => true;
    public bool Reject { get; init; }
    public int Applied { get; private set; }
    public XboxControllerState Last { get; private set; }
    public Task CreateAsync(int count, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyStateAsync(int index, XboxControllerState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Reject) throw new IOException("fixture rejection");
        Last = state; Applied++; return Task.CompletedTask;
    }
    public Task NeutralizeAsync(CancellationToken ct) { Last = default; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
