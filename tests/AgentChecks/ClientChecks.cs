using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static AgentFixture;

internal static class ClientChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("Python client bakes and submits a changed build through real HTTP", async () =>
        {
            await using var host = new AgentFixture();
            var package = Path.Combine(host.Root, "client-package");
            Directory.CreateDirectory(package);
            await File.WriteAllTextAsync(Path.Combine(package, "xemu.bin"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(package, "workload.bin"), "workload");
            await File.WriteAllTextAsync(Path.Combine(package, "job.json"), JsonSerializer.Serialize(new
            {
                Id = "seed", Executable = "xemu.bin", TimeoutSeconds = 10,
                RequiredFiles = new[] { "workload.bin" },
                Plan = Enumerable.Range(0, 1000).Select(_ => new { Type = "wait", DelayMs = 1 }).ToArray()
            }));
            var submitted = await Run(host, 0, "submit", package, "--id", "client-seed");
            Require(submitted.GetProperty("state").GetString() == "queued", "Client did not publish the initial package.");
            await Run(host, 0, "withdraw", "client-seed");
            var baked = await Run(host, 0, "bake", "client-smoke", "--from-job", "client-seed");
            var revision = baked.GetProperty("revision").GetString()!;
            var build = Path.Combine(host.Root, "changed-build");
            Directory.CreateDirectory(build);
            await File.WriteAllTextAsync(Path.Combine(build, "xemu.bin"), "candidate");
            var candidate = await Run(host, 0, "run", "client-smoke", "--revision", revision,
                "--id", "client-candidate", "--build", build);
            Require(candidate.GetProperty("state").GetString() == "queued", "Pre-baked client workflow did not submit.");
            var job = await host.Json("/api/v1/jobs/client-candidate");
            Require(job.GetProperty("job").GetProperty("plan").GetArrayLength() == 1000, "Client workflow lost the baked plan.");
            Require(job.GetProperty("job").GetProperty("expectedExecutableSha256").GetString() == Digest("candidate"), "New executable digest was not pinned.");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/client-seed/files/xemu.bin") == "fixture", "Source build changed.");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/client-candidate/files/workload.bin") == "workload", "Workload was not reused.");
        }));
        checks.Add(("Python result gate uses real failed assessment without collecting files", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("client-result");
            host.MoveToTesting("client-result", "client-run", "finalized");
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-client-result"), Path.Combine(host.Paths.Tested, "agent-client-result"));
            host.Assessment("client-run", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.Failed,
                EvidenceOutcome.Complete, ComparisonEligibility.Ineligible, ["guest_failed"],
                [new AssessmentCheck("guest", false, "correctness", "Fixture assertion failed.")]));
            var value = await Run(host, 2, "result", "client-result", "--require", "correctness");
            Require(value.GetProperty("outcome").GetProperty("correctness").GetString() == "failed", "Client did not preserve correctness.");
            var output = Path.Combine(host.Root, "selected-evidence");
            var receipt = await Run(host, 0, "collect", "client-result", output);
            Require(receipt.GetProperty("fileCount").GetInt32() == 1, "Default collection was not selected-only.");
            Require(File.Exists(Path.Combine(output, "client-run", "assessment.json")), "Requested assessment was not downloaded.");
        }));
    }

    private static async Task<JsonElement> Run(AgentFixture host, int expectedExit, params string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "runner_api.py"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate the repository's Python client.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("RUNNER_PYTHON") ?? "python")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root.FullName
            }
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(root.FullName, "scripts", "runner_api.py"));
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["XEMU_RUNNER_URL"] = host.Client.BaseAddress!.ToString().TrimEnd('/');
        Require(process.Start(), "Python client did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Python HTTP fixture exceeded its deadline.");
        }
        var output = await stdout;
        var error = await stderr;
        Require(process.ExitCode == expectedExit, $"Client exit {process.ExitCode}, expected {expectedExit}: {output} {error}");
        Require(error.Length == 0, "Client emitted unsolicited stderr: " + error);
        Require(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1, "Client stdout is not one JSON document.");
        Require(Encoding.UTF8.GetByteCount(output) <= 4096, "Routine client output exceeds 4 KiB.");
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }
}
