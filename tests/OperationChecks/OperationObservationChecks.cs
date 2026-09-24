using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Networking;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

internal static class OperationObservationChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("operation completion during a poll is not a fictitious restart", () =>
        {
            using var fixture = new Fixture();
            object? observed = null;
            Exception? failure = null;
            var observer = new Thread(() =>
            {
                try { observed = fixture.Read(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "operation-receipt-observer" };
            bool blocked;
            lock (fixture.Gate)
            {
                fixture.Running.Add(Fixture.Id);
                fixture.Write("running");
                observer.Start();
                // Control the real ownership gate instead of hoping a timing
                // stress loop hits the small read-receipt/remove-owner window.
                // Before the fix, the observer reads the old receipt then parks
                // here. With a coherent snapshot it parks before that read.
                blocked = SpinWait.SpinUntil(() =>
                    (observer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5));
                if (blocked)
                {
                    fixture.Write("completed");
                    fixture.Running.Remove(Fixture.Id);
                }
            }
            Require(observer.Join(TimeSpan.FromSeconds(5)), "The receipt observer did not terminate.");
            Require(blocked, "The observer did not reach the ownership gate.");
            if (failure is not null) throw new InvalidOperationException("Receipt read failed.", failure);
            Require(State(observed) == "completed", "A completed operation was reported as " + State(observed));
            return Task.CompletedTask;
        }));
        foreach (var (stored, owned, expected) in new[]
        {
            ("queued", false, "interrupted"), ("running", true, "running"),
            ("completed", false, "completed"), ("failed", false, "failed")
        })
        {
            checks.Add(($"operation {stored} with owned={owned} preserves {expected}", () =>
            {
                using var fixture = new Fixture();
                fixture.Write(stored);
                if (owned) fixture.Running.Add(Fixture.Id);
                Require(State(fixture.Read()) == expected, "Recovery changed a genuine operation state.");
                return Task.CompletedTask;
            }));
        }
        checks.Add(("published submit remains completed without an active receipt writer", () =>
        {
            using var fixture = new Fixture();
            fixture.Write("running", "submit");
            Directory.Move(Path.Combine(fixture.Home, "payload"), Path.Combine(fixture.Paths.Pending, "agent-" + Fixture.Id));
            Require(State(fixture.Read()) == "completed", "Published work was reported safe to resubmit.");
            return Task.CompletedTask;
        }));
    }

    private static string? State(object? value) => value?.GetType().GetProperty("State")?.GetValue(value) as string;
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    // White-box synchronization is confined to this fixture. No public test
    // hook, actual process, HTTP listener or replacement executor is introduced.
    private sealed class Fixture : IDisposable
    {
        public const string Id = "receipt-race";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "operation-checks-" + Guid.NewGuid().ToString("N"));
        private readonly XemuControlManager _control = new(new XemuControlOptions());
        private readonly object _store;
        public RunnerPaths Paths { get; }
        public object Gate { get; }
        public HashSet<string> Running { get; }
        public string Home => Path.Combine(Paths.Pending, ".agent-jobs", Id);

        public Fixture()
        {
            Paths = new RunnerPaths(Path.Combine(_root, "runner.json"), _root,
                Path.Combine(_root, "Pending"), Path.Combine(_root, "Testing"),
                Path.Combine(_root, "Tested"), Path.Combine(_root, "Results"), Path.Combine(_root, "Files"));
            foreach (var path in new[] { Paths.Pending, Paths.Testing, Paths.Tested, Paths.Results, Paths.FileRoot })
                Directory.CreateDirectory(path);
            var server = new EmbeddedHttpServer(new HttpOptions(), new UiOptions(), Paths, new RunnerState(),
                new JobQueue(new RunnerConfig(), Paths), _control, () => { });
            _store = typeof(EmbeddedHttpServer).GetProperty("AgentJobs", Private)!.GetValue(server)!;
            var type = _store.GetType();
            Gate = type.GetField("_gate", Private)!.GetValue(_store)!;
            Running = (HashSet<string>)type.GetField("_runningOperations", Private)!.GetValue(_store)!;
            var create = type.GetMethod("Create")!;
            var json = JsonSerializer.Serialize(new
            {
                id = Id,
                job = new { id = Id, executable = "xemu.bin", timeoutSeconds = 10, plan = Array.Empty<object>() },
                files = new[] { new { path = "xemu.bin", length = 7,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture"))).ToLowerInvariant(), executable = true } }
            });
            var request = JsonSerializer.Deserialize(json, create.GetParameters()[0].ParameterType, ConfigLoader.JsonOptions)!;
            _ = create.Invoke(_store, [request]);
        }

        public object? Read() => _store.GetType().GetMethod("GetOperation")!.Invoke(_store, [Id]);
        public void Write(string state, string action = "reuse") => AtomicJson.Write(Path.Combine(Home, "operation.json"), new
        {
            id = "fixed-operation", jobId = Id, action, state,
            createdUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        });
        public void Dispose()
        {
            _control.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
