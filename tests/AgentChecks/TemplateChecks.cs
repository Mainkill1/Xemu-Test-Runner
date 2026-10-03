using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class TemplateChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("test catalog lists compact pinned definitions", async () =>
        {
            await using var host = new AgentFixture();
            var empty = await host.Json("/api/v1/tests");
            Require(empty.GetProperty("items").GetArrayLength() == 0, "New catalog is not empty.");
            Require(empty.GetProperty("issues").GetArrayLength() == 0, "New catalog reported a phantom integrity issue.");
            var revision = await Bake(host);
            var list = await host.Json("/api/v1/tests");
            Require(list.GetProperty("items").GetArrayLength() == 1, "Baked definition not listed.");
            var item = list.GetProperty("items")[0];
            Require(item.GetProperty("revision").GetString() == revision, "Catalog did not pin the revision.");
            Require(!item.TryGetProperty("job", out _) && !item.TryGetProperty("files", out _), "Catalog repeated a large plan/manifest.");
        }));
        checks.Add(("saved test revisions survive host newline changes", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            var home = Path.Combine(host.Paths.Pending, ".agent-tests", "smoke");
            var currentPath = Path.Combine(home, revision + ".json");
            var stored = JsonNode.Parse(await File.ReadAllTextAsync(currentPath))!.AsObject();
            var definition = stored["Definition"] ?? throw new InvalidDataException("Fixture definition is missing.");
            var legacyCheck = JsonSerializer.SerializeToNode(new ArtifactCheckDefinition
            {
                Name = "legacy-artifact",
                Scope = "result",
                Path = "legacy.txt"
            }, ConfigLoader.JsonOptions)!.AsObject();
            legacyCheck.Remove(nameof(ArtifactCheckDefinition.ExpectedImageDHash));
            legacyCheck.Remove(nameof(ArtifactCheckDefinition.MaximumImageHammingDistance));
            definition["Job"]!["Workload"]!["CorrectnessChecks"]!.AsArray().Add(legacyCheck);
            var serialized = definition.ToJsonString(ConfigLoader.JsonOptions);
            var portable = serialized.Replace("\r\n", "\n", StringComparison.Ordinal);
            var portableRevision = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(portable))).ToLowerInvariant();
            var legacyBytes = Encoding.UTF8.GetBytes(portable.Replace("\n", "\r\n", StringComparison.Ordinal));
            var legacyRevision = Convert.ToHexString(SHA256.HashData(legacyBytes)).ToLowerInvariant();
            Require(legacyRevision != portableRevision, "Fixture did not produce a distinct Windows newline revision.");
            stored["Revision"] = legacyRevision;
            AtomicJson.Write(Path.Combine(home, legacyRevision + ".json"), stored);

            var restored = await host.Json($"/api/v1/tests/smoke/{legacyRevision}");
            Require(restored.GetProperty("summary").GetProperty("revision").GetString() == legacyRevision,
                "Legacy saved test lost its immutable revision.");
        }));
        checks.Add(("test catalog quarantines corrupt saved definitions", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            var home = Path.Combine(host.Paths.Pending, ".agent-tests", "smoke");
            var path = Path.Combine(home, revision + ".json");
            var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            stored["Definition"]!["Description"] = "tampered after revision assignment";
            AtomicJson.Write(path, stored);

            var catalog = await host.Json("/api/v1/tests");
            Require(catalog.GetProperty("items").GetArrayLength() == 0,
                "Catalog advertised a test whose immutable definition is corrupt.");
            var issue = catalog.GetProperty("issues").EnumerateArray().Single();
            Require(issue.GetProperty("id").GetString() == "smoke" &&
                    issue.GetProperty("revision").GetString() == revision,
                "Catalog warning did not identify the quarantined revision.");
        }));
        checks.Add(("pre-baked test expands a large plan from a small request", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host, 1000);
            var request = new { Id = "candidate", TestId = "smoke", Revision = revision };
            Require(JsonSerializer.SerializeToUtf8Bytes(request).Length < 256, "Reference request grew into a plan upload.");
            var receipt = await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, request, HttpStatusCode.Accepted);
            Require(!receipt.TryGetProperty("job", out _), "Preparation receipt echoed the expanded plan.");
            await WaitOperation(host, "candidate");
            var candidate = await host.Json("/api/v1/jobs/candidate");
            Require(candidate.GetProperty("job").GetProperty("plan").GetArrayLength() == 1000, "Server did not preserve the complete baked plan.");
            Require(candidate.GetProperty("job").GetProperty("tags").EnumerateArray().Any(tag => tag.GetString() == "test-definition:smoke@" + revision), "Frozen job lost definition provenance.");
            var bytes = await host.Client.GetStringAsync("/api/v1/jobs/candidate/files/workload.bin");
            Require(bytes == "workload", "Unchanged workload was not copied on the tester.");
        }));
        checks.Add(("changed build uploads only changed bytes and preserves source", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, new
            {
                Id = "changed", TestId = "smoke", Revision = revision,
                Files = new[] { new { Path = "xemu.bin", Length = 9, Sha256 = Digest("candidate"), Executable = true } }
            }, HttpStatusCode.Accepted);
            await WaitOperation(host, "changed");
            var executable = await host.Json("/api/v1/jobs/changed/files/xemu.bin?upload-status=1");
            Require(!executable.GetProperty("complete").GetBoolean(), "Old executable was falsely reused for new content.");
            var workload = await host.Json("/api/v1/jobs/changed/files/workload.bin?upload-status=1");
            Require(workload.GetProperty("complete").GetBoolean(), "Unchanged workload requires another upload.");
            await Upload(host, "changed", "xemu.bin", "candidate");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/seed/files/xemu.bin") == "fixture", "Source build was overwritten.");
            await host.Json("/api/v1/jobs/changed/validate", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            await WaitOperation(host, "changed");
            var validation = await host.Json("/api/v1/jobs/changed/validation");
            Require(validation.GetProperty("passed").GetBoolean(), "Changed executable was validated against the old build hash.");
        }));
        checks.Add(("pinned test rejects undeclared workload replacement", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            var error = await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, new
            {
                Id = "bad-input", TestId = "smoke", Revision = revision,
                Files = new[] { new { Path = "workload.bin", Length = 7, Sha256 = Digest("changed") } }
            }, HttpStatusCode.BadRequest);
            Require(error.GetProperty("code").GetString() == "test_file_not_replaceable", "Fixed workload replacement was not explained.");
            Require(!Directory.Exists(host.Draft("bad-input")), "Rejected request left an executable draft.");
        }));
        checks.Add(("test execution requires an explicit existing revision", async () =>
        {
            await using var host = new AgentFixture();
            await Bake(host);
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, new { Id = "unpinned", TestId = "smoke" }, (HttpStatusCode)428);
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post,
                new { Id = "unknown", TestId = "smoke", Revision = new string('0', 64) }, HttpStatusCode.NotFound);
        }));
        checks.Add(("baking new revisions leaves older plans unchanged", async () =>
        {
            await using var host = new AgentFixture();
            var oldRevision = await Bake(host, 20);
            var source = await host.Json("/api/v1/jobs/seed");
            var job = JsonNode.Parse(source.GetProperty("job").GetRawText())!.AsObject();
            job["timeoutSeconds"] = 99;
            using var edit = new HttpRequestMessage(HttpMethod.Put, "/api/v1/jobs/seed/plan");
            edit.Headers.TryAddWithoutValidation("If-Match", source.GetProperty("revision").GetString());
            edit.Content = new StringContent(job.ToJsonString(), Encoding.UTF8, "application/json");
            using var edited = await host.Client.SendAsync(edit);
            Require(edited.StatusCode == HttpStatusCode.OK, "Fixture plan edit failed.");
            var updated = await host.Json("/api/v1/tests/smoke/bake", HttpMethod.Post, new { SourceJobId = "seed" });
            Require(updated.GetProperty("revision").GetString() != oldRevision, "Changed definition retained its content revision.");
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post,
                new { Id = "old-revision", TestId = "smoke", Revision = oldRevision }, HttpStatusCode.Accepted);
            await WaitOperation(host, "old-revision");
            var old = await host.Json("/api/v1/jobs/old-revision");
            Require(old.GetProperty("job").GetProperty("timeoutSeconds").GetInt32() == 10, "Old revision resolved to the latest mutable plan.");
        }));
        checks.Add(("lost preparation response reuses the same draft identity", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            var request = new { Id = "retry-id", TestId = "smoke", Revision = revision };
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, request, HttpStatusCode.Accepted);
            await WaitOperation(host, "retry-id");
            var again = await host.Json("/api/v1/jobs/from-test", HttpMethod.Post, request, HttpStatusCode.Accepted);
            Require(again.GetProperty("id").GetString() == "retry-id", "Retry changed job identity.");
            await WaitOperation(host, "retry-id");
            var jobs = await host.Json("/api/v1/jobs");
            Require(jobs.GetProperty("items").GetArrayLength() == 2, "Retry created another attempt.");
        }));
        checks.Add(("pre-baked execution does not bypass benchmark policy", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            host.State.BeginJob("benchmark", "active", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            var error = await host.Json("/api/v1/jobs/from-test", HttpMethod.Post,
                new { Id = "blocked", TestId = "smoke", Revision = revision }, HttpStatusCode.Conflict);
            Require(error.GetProperty("code").GetString() == "operation_blocked", "Benchmark refusal was lost.");
            Require(!Directory.Exists(host.Draft("blocked")), "Refused preparation performed a mutation.");
        }));
        checks.Add(("test reference rejects silently ignored plan overrides", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Bake(host);
            await host.Json("/api/v1/jobs/from-test", HttpMethod.Post,
                new { Id = "override", TestId = "smoke", Revision = revision, Plan = new[] { new { Type = "quit" } } }, HttpStatusCode.BadRequest);
        }));
    }

    private static async Task<string> Bake(AgentFixture host, int steps = 5)
    {
        await host.Json("/api/v1/jobs", HttpMethod.Post, new
        {
            Id = "seed", Job = new
            {
                Id = "seed", Executable = "xemu.bin", ExpectedExecutableSha256 = Digest("fixture"), TimeoutSeconds = 10,
                RequiredFiles = new[] { "workload.bin" },
                Plan = Enumerable.Range(0, steps).Select(_ => new { Type = "wait", DelayMs = 1 }).ToArray()
            },
            Files = new[]
            {
                new { Path = "xemu.bin", Length = 7, Sha256 = Digest("fixture"), Executable = true },
                new { Path = "workload.bin", Length = 8, Sha256 = Digest("workload"), Executable = false }
            }
        });
        await Upload(host, "seed", "xemu.bin", "fixture");
        await Upload(host, "seed", "workload.bin", "workload");
        var baked = await host.Json("/api/v1/tests/smoke/bake", HttpMethod.Post, new { SourceJobId = "seed" });
        return baked.GetProperty("revision").GetString()!;
    }

    private static async Task Upload(AgentFixture host, string id, string path, string value)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        using var response = await host.Client.PutAsync($"/api/v1/jobs/{id}/files/{path}", content);
        Require(response.StatusCode == HttpStatusCode.Created, "Fixture upload failed: " + await response.Content.ReadAsStringAsync());
    }

    private static async Task WaitOperation(AgentFixture host, string id)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end)
        {
            var operation = await host.Json($"/api/v1/jobs/{id}/operation");
            var state = operation.GetProperty("state").GetString();
            if (state == "completed")
            {
                var job = await host.Json($"/api/v1/jobs/{id}?view=summary");
                if (job.GetProperty("state").GetString() != "busy") return;
            }
            else Require(state is "queued" or "running", operation.GetRawText());
            await Task.Delay(30);
        }
        throw new TimeoutException("Fixture operation did not finish.");
    }
}
