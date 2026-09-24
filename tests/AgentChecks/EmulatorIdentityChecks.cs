using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class EmulatorIdentityChecks
{
    private static object Job(string id) => new { id, executable = "launch-xemu.sh", requiredFiles = new[] { "xemu" }, timeoutSeconds = 10 };
    private static async Task Setup(AgentFixture host)
    {
        foreach (var pair in new[] { (Id: "wrapper-seed", Binary: "baseline"), (Id: "wrapper-app", Binary: "candidate") })
        {
            await host.Json("/api/v1/jobs", HttpMethod.Post, new
            {
                id = pair.Id, job = Job(pair.Id), files = new[]
                {
                    new { path = "launch-xemu.sh", length = 7, sha256 = Digest("wrapper"), executable = true },
                    new { path = "xemu", length = pair.Binary.Length, sha256 = Digest(pair.Binary), executable = true }
                }
            });
            foreach (var file in new[] { (Path: "launch-xemu.sh", Text: "wrapper"), (Path: "xemu", Text: pair.Binary) })
            {
                using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Text));
                using var response = await host.Client.PutAsync($"/api/v1/jobs/{pair.Id}/files/{file.Path}", content);
                Require(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
        }
    }

    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("baking a wrapper cannot omit its declared emulator build slot", async () =>
        {
            await using var host = new AgentFixture();
            await Setup(host);
            var invalid = await host.Json("/api/v1/tests/wrapper-smoke/bake", HttpMethod.Post,
                new { sourceJobId = "wrapper-seed", buildFiles = new[] { "launch-xemu.sh" } }, HttpStatusCode.BadRequest);
            Require(invalid.GetProperty("code").GetString() == "emulator_build_slot_required", "Missing emulator slot was not diagnosed.");
            var uploaded = await host.Json("/api/v1/test-configs/wrapper-custom", HttpMethod.Post,
                new { sourceJobId = "wrapper-seed", job = Job("ignored"), buildFiles = new[] { "launch-xemu.sh" } }, HttpStatusCode.BadRequest);
            Require(uploaded.GetProperty("code").GetString() == "emulator_build_slot_required", "Config upload bypassed the guard.");
        }));
        checks.Add(("default wrapper build slots include native xemu and preserve hash-pinned input provenance", async () =>
        {
            await using var host = new AgentFixture();
            await Setup(host);
            var baked = await host.Json("/api/v1/tests/wrapper-smoke/bake", HttpMethod.Post, new { sourceJobId = "wrapper-seed" });
            var revision = baked.GetProperty("revision").GetString();
            var detail = await host.Json("/api/v1/tests/wrapper-smoke/" + revision);
            Require(detail.GetProperty("buildFiles").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "launch-xemu.sh", "xemu" }), "Default slots kept only the launcher.");
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "wrapper-candidate", applicationJobId = "wrapper-app", testId = "wrapper-smoke", revision });
            await host.Json("/api/v1/test-runs/wrapper-candidate/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            await RequestedTestChecks.Until(host, "wrapper-candidate", "queuedForExecution");
            var candidate = await host.Json("/api/v1/jobs/wrapper-candidate");
            var input = candidate.GetProperty("job").GetProperty("inputs").EnumerateArray().Single(x => x.GetProperty("path").GetString() == "xemu");
            Require(input.GetProperty("expectedSha256").GetString() == Digest("candidate") && input.GetProperty("hash").GetBoolean(), "Actual emulator identity was not pinned.");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/wrapper-candidate/files/xemu") == "candidate", "Candidate still contains the source xemu.");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/wrapper-seed/files/xemu") == "baseline", "Source package was mutated.");
            var identity = await host.Json("/api/v1/applications/wrapper-app");
            Require(identity.GetProperty("sha256").GetString() == Digest("candidate") && identity.GetProperty("launchSha256").GetString() == Digest("wrapper"), "Application summary conflates launcher and emulator.");
        }));
        checks.Add(("partial replacement of only a launcher cannot silently reuse the old native binary", async () =>
        {
            await using var host = new AgentFixture();
            await Setup(host);
            var baked = await host.Json("/api/v1/tests/wrapper-smoke/bake", HttpMethod.Post,
                new { sourceJobId = "wrapper-seed", buildFiles = new[] { "launch-xemu.sh", "xemu" } });
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, new
            {
                id = "wrong-native", testId = "wrapper-smoke", revision = baked.GetProperty("revision").GetString(),
                files = new[] { new { path = "launch-xemu.sh", length = 7, sha256 = Digest("wrapper"), executable = true } }
            }, HttpStatusCode.BadRequest);
        }));
        checks.Add(("hash history indexes verified emulator bytes separately from the wrapper", async () =>
        {
            await using var host = new AgentFixture();
            host.State.SetPhase("fixture");
            await Setup(host);
            var package = host.Draft("wrapper-app");
            var jobFile = Path.Combine(package, "job.json");
            var job = JsonSerializer.Deserialize<XemuTestRunner.Queue.JobDefinition>(await File.ReadAllTextAsync(jobFile), ConfigLoader.JsonOptions)!;
            job.Inputs.Add(new InputIdentityDefinition { Path = "xemu", Role = "emulator", Hash = true, ExpectedSha256 = Digest("candidate") });
            AtomicJson.Write(jobFile, job);
            var jobHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(jobFile))).ToLowerInvariant();
            host.MoveToTesting("wrapper-app", "wrapper-run", "finalized");
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-wrapper-app"), Path.Combine(host.Paths.Tested, "agent-wrapper-app"));
            var root = Path.Combine(host.Paths.Results, "wrapper-run"); Directory.CreateDirectory(root);
            AtomicJson.Write(Path.Combine(host.Paths.Pending, ".agent-jobs", "wrapper-app", "validation.json"), new { Passed = true, ExecutableSha256 = Digest("wrapper"), Checks = Array.Empty<object>() });
            host.Assessment("wrapper-run", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.Passed, EvidenceOutcome.Complete, ComparisonEligibility.Eligible, [], []));
            AtomicJson.Write(Path.Combine(root, "input-manifest.json"), new
            {
                JobId = "wrapper-app", ExecutableSha256 = Digest("wrapper"), JobManifestSha256 = jobHash,
                Inputs = new[] { new { Path = "xemu", Role = "emulator", Sha256 = Digest("candidate"), ExpectedSha256 = Digest("candidate"), Verified = true } }
            });
            AtomicJson.Write(Path.Combine(root, "result.json"), new
            {
                runId = "wrapper-run", job = "wrapper-app", executableSha256 = Digest("wrapper"),
                workload = new { Measurements = new[] { new { Name = "runtime", Value = 20.0, Unit = "ms", Direction = "lower" } } },
                host = new { runnerVersion = "fixture" }, monitoring = new { intervalMs = 100, gpuProviders = Array.Empty<string>() }
            });
            AtomicJson.Write(Path.Combine(root, "host-inventory.json"), new
            {
                Machine = "fixture", OperatingSystem = "fixture", OsArchitecture = "X64", ProcessArchitecture = "X64",
                DotNet = "fixture", CpuModel = "fixture", LogicalProcessors = 4, GraphicsAdapters = Array.Empty<object>()
            });
            var indexed = await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId = "wrapper-run" });
            Require(indexed.GetProperty("sha256").GetString() == Digest("candidate"), "History was keyed by wrapper bytes.");
            var summary = await host.Json("/api/v1/build-results/" + Digest("candidate"));
            Require(summary.GetProperty("runCount").GetInt32() == 1, "Emulator-hash history lost the attempt.");
        }));
    }
}
