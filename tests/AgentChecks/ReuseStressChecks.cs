using System.Diagnostics;
using System.Text.Json;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class ReuseStressChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("repeated large-plan reuse while polling preserves publication", async () =>
        {
            var originals = new List<(string Name, Func<Task> Run)>();
            TemplateChecks.Register(originals);
            var exercise = originals.Single(check => check.Name == "pre-baked test expands a large plan from a small request");
            for (var iteration = 1; iteration <= 12; iteration++)
            {
                try { await exercise.Run(); }
                catch (Exception error) { throw new InvalidOperationException($"Reuse stress iteration {iteration}: {error.Message}", error); }
            }
        }));
        checks.Add(("atomic metadata publication survives a brief Windows reader lock", async () =>
        {
            // Windows denies replacement while a reader has not shared delete.
            // Linux permits unlink of an open file, so its sharing semantics do
            // not reproduce this particular contention condition.
            if (!OperatingSystem.IsWindows()) return;
            var root = Path.Combine(Path.GetTempPath(), "atomic-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "operation.json");
            try
            {
                AtomicJson.Write(path, new { state = "old" });
                using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    blocker.Dispose();
                });
                try { AtomicJson.Write(path, new { state = "new" }); }
                finally { await release; }
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                Require(document.RootElement.GetProperty("state").GetString() == "new", "New metadata was not published after the reader released its lock.");
                Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Metadata publication left a temporary file.");
            }
            finally { Directory.Delete(root, true); }
        }));
        checks.Add(("persistent metadata contention fails without replacing the previous record", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var root = Path.Combine(Path.GetTempPath(), "atomic-blocked-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "operation.json");
            try
            {
                AtomicJson.Write(path, new { state = "old" });
                var timer = Stopwatch.StartNew();
                Exception? failure = null;
                using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { AtomicJson.Write(path, new { state = "new" }); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure = error; }
                }
                Require(failure is not null, "Blocked publication was falsely reported as successful.");
                Require(timer.Elapsed < TimeSpan.FromSeconds(5), "Persistent denial was retried without a bound.");
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                Require(document.RootElement.GetProperty("state").GetString() == "old", "Failed publication destroyed the previous complete record.");
                Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Failed metadata publication left its staging file.");
            }
            finally { Directory.Delete(root, true); }
        }));
    }
}
