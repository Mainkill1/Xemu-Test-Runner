using System.Diagnostics;
using System.Text.Json;
using static AgentFixture;

internal static class RequestedClientChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("focused Python upload waits for explicit start against the actual listener", async () =>
        {
            await using var host = new AgentFixture();
            await RequestedTestChecks.Setup(host);
            var folder = Path.Combine(host.Root, "focused-app");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "different.exe"), "client-binary");
            var upload = await Run(host, "upload", folder, "--exe", "different.exe", "--id", "focus-app", "--tests", "smoke");
            Require(!upload.GetProperty("startRequested").GetBoolean(), "Upload inferred permission to start.");
            Require(upload.GetProperty("sha256").GetString() == Digest("client-binary"), "Wrong application hash.");
            await Task.Delay(400);
            Require((await host.Json("/api/v1/test-runs/focus-app-t001")).GetProperty("state").GetString() == "uploaded", "Unrequested test executed.");
            host.State.BeginJob("active", "active-run", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            var start = await Run(host, "start", "focus-app-t001");
            Require(start.GetProperty("tests")[0].GetProperty("state").GetString() == "queued", "Explicit start did not queue behind the benchmark.");
            host.State.EndJob("completed");
            await RequestedTestChecks.Until(host, "focus-app-t001", "queuedForExecution");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/focus-app-t001/files/xemu.bin") == "client-binary", "Renamed application was not materialized correctly.");
        }));
    }

    private static async Task<JsonElement> Run(AgentFixture host, params string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "runner_tests.py"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate the focused test client.");
        using var process = new Process { StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("RUNNER_PYTHON") ?? "python")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root.FullName
        }};
        process.StartInfo.ArgumentList.Add(Path.Combine(root.FullName, "scripts", "runner_tests.py"));
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["XEMU_RUNNER_URL"] = host.Client.BaseAddress!.ToString().TrimEnd('/');
        Require(process.Start(), "Cannot start local HTTP test client.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Focused client exceeded its fixture deadline.");
        }
        var output = await stdout;
        var error = await stderr;
        Require(process.ExitCode == 0 && error.Length == 0, output + error);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }
}
