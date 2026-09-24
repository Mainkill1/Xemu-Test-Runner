using System.Net;
using System.Security.Cryptography;
using System.Text;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class CrashApiChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("crash summaries are small and ZIP download remains a ranged secondary action", async () =>
        {
            await using var host = new AgentFixture();
            var root = Path.Combine(host.Paths.Results, "crash-http");
            Directory.CreateDirectory(root);
            var bytes = Encoding.UTF8.GetBytes("fixture ZIP bytes");
            await File.WriteAllBytesAsync(Path.Combine(root, "diagnostics.zip"), bytes);
            AtomicJson.Write(Path.Combine(root, "crash", "report.json"), new CrashReport("crash-http", Digest("fixture"),
                "linux", 42, 139, false, true, "waitpid", "SIGSEGV", "partial", "unavailable", "unavailable", [], ["core_unavailable"]));
            AtomicJson.Write(Path.Combine(root, "diagnostic-bundle.json"), new DiagnosticBundle("partial", "diagnostics.zip",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, 2, 1, "fixture.zip", "captured", []));
            var summary = await host.Json("/api/v1/runs/crash-http/diagnostics");
            Require(summary.GetProperty("crash").GetProperty("crashed").GetBoolean(), "Crash outcome missing.");
            Require(summary.GetProperty("bundle").GetProperty("state").GetString() == "partial", "Partial diagnostics promoted to complete.");
            Require(Encoding.UTF8.GetByteCount(summary.GetRawText()) <= 4096, "Summary exceeds budget.");
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/crash-http/artifacts/diagnostics.zip");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
            using var response = await host.Client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.PartialContent, "ZIP range unavailable.");
            Require((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[2..6]), "Wrong ZIP range bytes.");
            host.State.BeginJob("benchmark", "active", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            await host.Json("/api/v1/runs/crash-http/diagnostics");
            using var raw = await host.Client.GetAsync("/api/v1/runs/crash-http/artifacts/diagnostics.zip");
            Require(raw.StatusCode == HttpStatusCode.Conflict, "Raw ZIP ignored benchmark transfer policy.");
        }));
        checks.Add(("an early crash without telemetry remains in executable-hash history", async () =>
        {
            await using var host = new AgentFixture();
            host.State.SetPhase("fixture");
            await host.CreateDraft("early-crash");
            var package = Path.Combine(host.Paths.Tested, "agent-early-crash");
            Directory.Move(host.Draft("early-crash"), package);
            AtomicJson.Write(Path.Combine(package, ".runner-attempt.json"), new { RunId = "early-run", Phase = "finalized", Attempt = 1 });
            var sha = Digest("fixture");
            host.Assessment("early-run", new RunAssessment(ExecutionOutcome.Crashed, CorrectnessOutcome.Incomplete,
                EvidenceOutcome.Incomplete, ComparisonEligibility.Ineligible, ["crashed"], []));
            var resultRoot = Path.Combine(host.Paths.Results, "early-run");
            AtomicJson.Write(Path.Combine(resultRoot, "result.json"), new
            {
                runId = "early-run", job = "early-crash", status = "crashed", executableSha256 = sha,
                workload = new { Measurements = Array.Empty<object>() }, monitoring = (object?)null,
                host = new { runnerVersion = "fixture" }
            });
            var planHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(package, "job.json")))).ToLowerInvariant();
            AtomicJson.Write(Path.Combine(resultRoot, "input-manifest.json"), new { JobId = "early-crash", ExecutableSha256 = sha, JobManifestSha256 = planHash });
            AtomicJson.Write(Path.Combine(resultRoot, "host-inventory.json"), new
            {
                Machine = "fixture", OperatingSystem = "fixture", OsArchitecture = "X64", ProcessArchitecture = "X64",
                DotNet = "fixture", CpuModel = "fixture", LogicalProcessors = 2, GraphicsAdapters = Array.Empty<object>()
            });
            await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId = "early-run" });
            var summary = await host.Json("/api/v1/build-results/" + sha);
            Require(summary.GetProperty("runCount").GetInt32() == 1, "Unmeasured crash vanished.");
            Require(summary.GetProperty("eligibleRuns").GetInt32() == 0, "Crash became a performance pass.");
            Require(summary.GetProperty("runs")[0].GetProperty("outcome").GetProperty("execution").GetString() == "crashed", "Hash report lost crash identity.");
            Require(summary.GetProperty("runs")[0].GetProperty("diagnostics").GetString()!.EndsWith("/diagnostics"), "Hash report has no diagnostic drilldown.");
            await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = sha }, HttpStatusCode.Conflict);
        }));
        checks.Add(("crash readiness reports prerequisites without starting a target", async () =>
        {
            await using var host = new AgentFixture();
            var value = await host.Json("/api/v1/diagnostics/crash-capabilities");
            Require(value.GetProperty("enabled").GetBoolean(), "Crash reporting should be enabled by default.");
            Require(value.GetProperty("tools").ValueKind == System.Text.Json.JsonValueKind.Array, "Missing provider readiness.");
            Require(host.State.Snapshot().CurrentJob is null, "Capability read started a target.");
        }));
    }
}
