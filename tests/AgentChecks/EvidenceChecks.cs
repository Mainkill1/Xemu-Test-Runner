using System.Net;
using System.Text.Json;
using static AgentFixture;

internal static class EvidenceChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("artifact pagination does not silently stop at one thousand", async () =>
        {
            await using var host = new AgentFixture();
            var root = Path.Combine(host.Paths.Results, "many");
            Directory.CreateDirectory(root);
            for (var i = 0; i < 1007; i++) await File.WriteAllTextAsync(Path.Combine(root, $"item-{i:D4}.txt"), "evidence");
            string? cursor = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await host.Json("/api/v1/runs/many/artifacts?limit=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
                Require(page.GetProperty("items").GetArrayLength() <= 100, "Page exceeds limit.");
                foreach (var item in page.GetProperty("items").EnumerateArray())
                    Require(names.Add(item.GetProperty("path").GetString()!), "Pagination duplicated an artifact.");
                Require(page.GetProperty("complete").GetBoolean(), "Regular fixture enumeration was incomplete.");
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor is not null);
            Require(names.Count == 1007, "Artifacts beyond the old cap were lost.");
        }));
        checks.Add(("artifact cursors cannot switch runs or resume an unknown snapshot", async () =>
        {
            await using var host = new AgentFixture();
            foreach (var run in new[] { "first", "second" })
            {
                var root = Path.Combine(host.Paths.Results, run);
                Directory.CreateDirectory(root);
                await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a");
                await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "b");
            }
            var first = await host.Json("/api/v1/runs/first/artifacts?limit=1");
            var cursor = first.GetProperty("nextCursor").GetString()!;
            var wrong = await host.Json("/api/v1/runs/second/artifacts?cursor=" + Uri.EscapeDataString(cursor), expected: HttpStatusCode.Conflict);
            Require(wrong.GetProperty("code").GetString() == "artifact_cursor_invalid", "Cross-run cursor was not rejected.");
            await host.Json("/api/v1/runs/first/artifacts?cursor=expired", expected: HttpStatusCode.Conflict);
        }));
        checks.Add(("artifact limits and depth exclusions are explicit", async () =>
        {
            await using var host = new AgentFixture();
            var root = Path.Combine(host.Paths.Results, "deep");
            Directory.CreateDirectory(root);
            var deep = root;
            for (var i = 0; i < 20; i++) deep = Path.Combine(deep, "nested");
            Directory.CreateDirectory(deep);
            await File.WriteAllTextAsync(Path.Combine(deep, "hidden-by-depth.txt"), "evidence");
            var page = await host.Json("/api/v1/runs/deep/artifacts");
            Require(!page.GetProperty("complete").GetBoolean(), "Depth exclusion was silently described as complete collection.");
            Require(page.GetProperty("issues").GetArrayLength() > 0, "Incomplete enumeration has no explanation.");
            await host.Json("/api/v1/runs/deep/artifacts?limit=101", expected: HttpStatusCode.BadRequest);
        }));
        checks.Add(("log cursor returns new bytes rather than repeated tails", async () =>
        {
            await using var host = new AgentFixture();
            var root = Path.Combine(host.Paths.Results, "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "stderr.log");
            await File.WriteAllTextAsync(path, "first\n");
            var first = await host.Json("/api/v1/runs/logs/log?file=stderr.log&bytes=4096");
            Require(first.GetProperty("text").GetString() == "first\n", "First log slice is wrong.");
            var cursor = first.GetProperty("nextCursor").GetString()!;
            await File.AppendAllTextAsync(path, "second\n");
            var second = await host.Json("/api/v1/runs/logs/log?file=stderr.log&bytes=4096&cursor=" + Uri.EscapeDataString(cursor));
            Require(second.GetProperty("text").GetString() == "second\n", "Old log text was returned again.");
            var again = await host.Json("/api/v1/runs/logs/log?file=stderr.log&cursor=" + Uri.EscapeDataString(second.GetProperty("nextCursor").GetString()!));
            Require(again.GetProperty("bytes").GetInt32() == 0, "Unchanged log returned duplicate bytes.");
        }));
        checks.Add(("log truncation reports reset instead of skipping new evidence", async () =>
        {
            await using var host = new AgentFixture();
            var root = Path.Combine(host.Paths.Results, "reset");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "stdout.log");
            await File.WriteAllTextAsync(path, "a much longer previous log\n");
            var first = await host.Json("/api/v1/runs/reset/log?bytes=4096");
            await File.WriteAllTextAsync(path, "new\n");
            var next = await host.Json("/api/v1/runs/reset/log?cursor=" + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!));
            Require(next.GetProperty("reset").GetBoolean(), "Truncation was not surfaced.");
            Require(next.GetProperty("text").GetString() == "new\n", "New log was skipped after truncation.");
        }));
        checks.Add(("focused evidence rejects traversal and respects benchmark policy", async () =>
        {
            await using var host = new AgentFixture();
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "safe"));
            await host.Json("/api/v1/runs/safe/log?file=../secrets", expected: HttpStatusCode.BadRequest);
            host.State.BeginJob("benchmark", "active", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            var blocked = await host.Json("/api/v1/runs/safe/artifacts", expected: HttpStatusCode.Conflict);
            Require(blocked.GetProperty("code").GetString() == "operation_blocked", "Heavy listing bypassed benchmark policy.");
            await host.Json("/api/v1/runs/safe/log", expected: HttpStatusCode.Conflict);
        }));
    }
}
