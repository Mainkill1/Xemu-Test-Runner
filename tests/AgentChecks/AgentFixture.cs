using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Networking;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

internal sealed class AgentFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly XemuControlManager _control = new(new XemuControlOptions());
    private readonly Task _server;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "agent-checks-" + Guid.NewGuid().ToString("N"));
    public RunnerPaths Paths { get; }
    public RunnerState State { get; } = new();
    public HttpClient Client { get; }

    public AgentFixture()
    {
        Paths = new RunnerPaths(Path.Combine(Root, "runner.json"), Root,
            Path.Combine(Root, "Pending"), Path.Combine(Root, "Testing"),
            Path.Combine(Root, "Tested"), Path.Combine(Root, "Results"), Path.Combine(Root, "Files"));
        foreach (var path in new[] { Paths.Pending, Paths.Testing, Paths.Tested, Paths.Results, Paths.FileRoot })
            Directory.CreateDirectory(path);
        // Only this local fixture reserves a port and manipulates queue paths.
        // Clients under test have HTTP access, not a filesystem test shortcut.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var http = new HttpOptions { BindAddress = "127.0.0.1", Port = port };
        var server = new EmbeddedHttpServer(http, new UiOptions(), Paths, State,
            new JobQueue(new RunnerConfig(), Paths), _control, () => _stop.Cancel())
        {
            Reliability = new ReliabilityOptions { Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 } }
        };
        State.SetPhase("idle");
        _server = server.RunAsync(_stop.Token);
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public async Task<JsonElement> Json(string path, HttpMethod? method = null, object? body = null,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Require(response.StatusCode == expected, $"{method} {path}: expected {expected}, got {response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async Task CreateDraft(string id, int steps = 1)
    {
        await Json("/api/v1/jobs", HttpMethod.Post, new
        {
            Id = id,
            Job = new
            {
                Id = id, Executable = "xemu.bin", TimeoutSeconds = 10,
                Plan = Enumerable.Range(0, steps).Select(_ => new { Type = "wait", DelayMs = 1 }).ToArray()
            },
            Files = new[] { new { Path = "xemu.bin", Length = 7, Sha256 = Digest("fixture"), Executable = true } }
        });
    }

    public static string Digest(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    public string Draft(string id) => Path.Combine(Paths.Pending, ".agent-jobs", id, "payload");

    public void MoveToTesting(string id, string runId, string phase)
    {
        var testing = Path.Combine(Paths.Testing, "agent-" + id);
        Directory.Move(Draft(id), testing);
        AtomicJson.Write(Path.Combine(testing, ".runner-attempt.json"), new { RunId = runId, Phase = phase, Attempt = 1 });
    }

    public void Assessment(string runId, RunAssessment assessment)
    {
        var path = Path.Combine(Paths.Results, runId, "assessment.json");
        AtomicJson.Write(path, assessment);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _stop.Cancel();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        _control.Dispose();
        _stop.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
