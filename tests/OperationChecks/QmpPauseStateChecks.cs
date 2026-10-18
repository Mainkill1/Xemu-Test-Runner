using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;

internal static class QmpPauseStateChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        foreach (var (name, response, expected) in new (string, string, bool)[]
        {
            ("nonrunning prelaunch", "{\"status\":\"prelaunch\",\"running\":false}", true),
            ("nonrunning paused", "{\"status\":\"paused\",\"running\":false}", true),
            ("running", "{\"status\":\"running\",\"running\":true}", false),
            ("shutdown", "{\"status\":\"shutdown\",\"running\":false}", false)
        })
        {
            checks.Add(($"QMP pause refresh classifies {name}", async () =>
            {
                await using var server = new StatusServer(response);
                using var process = Process.GetCurrentProcess();
                using var control = NewControl(process, server.Port);
                await control.RefreshPauseStateAsync(server.Token);
                Require(control.Snapshot().Paused == expected, "Actual QMP status was misclassified as paused/unpaused.");
            }));
        }
        foreach (var (name, response) in new (string, string)[]
        {
            ("contradictory prelaunch", "{\"status\":\"prelaunch\",\"running\":true}"),
            ("contradictory paused", "{\"status\":\"paused\",\"running\":true}"),
            ("contradictory running", "{\"status\":\"running\",\"running\":false}"),
            ("missing running", "{\"status\":\"paused\"}"),
            ("string running", "{\"status\":\"paused\",\"running\":\"false\"}"),
            ("null running", "{\"status\":\"paused\",\"running\":null}"),
            ("missing status", "{\"running\":false}"),
            ("numeric status", "{\"status\":0,\"running\":false}"),
            ("null status", "{\"status\":null,\"running\":false}"),
            ("empty status", "{\"status\":\"\",\"running\":false}"),
            ("array result", "[]"),
            ("null result", "null")
        })
        {
            checks.Add(($"QMP pause refresh rejects {name} without releasing a paused waiter", async () =>
            {
                await using var server = new StatusServer("{\"status\":\"paused\",\"running\":false}");
                using var process = Process.GetCurrentProcess();
                using var control = NewControl(process, server.Port);
                await control.RefreshPauseStateAsync(server.Token);
                var waiter = control.DelayTestTimeAsync(1, server.Token);
                Require(!waiter.IsCompleted, "The paused test delay did not wait.");
                server.Response = response;
                var rejected = false;
                try { await control.RefreshPauseStateAsync(server.Token); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Malformed or contradictory QMP state was accepted.");
                Require(control.Snapshot().Paused && !waiter.IsCompleted,
                    "An invalid status response released the established pause state.");
                await control.ResumeAsync(server.Token);
                await waiter.WaitAsync(server.Token);
            }));
        }
        checks.Add(("QMP prelaunch refresh permits managed resume", async () =>
        {
            await using var server = new StatusServer("{\"status\":\"prelaunch\",\"running\":false}");
            using var process = Process.GetCurrentProcess();
            using var control = NewControl(process, server.Port);
            await control.RefreshPauseStateAsync(server.Token);
            Require(control.Snapshot().Paused, "StartPaused prelaunch was rejected before resume.");
            var waiter = control.DelayTestTimeAsync(1, server.Token);
            Require(!waiter.IsCompleted, "The prelaunch test delay did not wait.");
            await control.ResumeAsync(server.Token);
            await waiter.WaitAsync(server.Token);
            Require(!control.Snapshot().Paused, "Managed resume left the cached state paused.");
            Require(server.Continued, "Managed resume never sent cont to the owned QMP endpoint.");
        }));
        checks.Add(("QMP running refresh releases the cached pause state", async () =>
        {
            await using var server = new StatusServer("{\"status\":\"paused\",\"running\":false}");
            using var process = Process.GetCurrentProcess();
            using var control = NewControl(process, server.Port);
            await control.RefreshPauseStateAsync(server.Token);
            Require(control.Snapshot().Paused, "Initial valid paused response was not retained.");
            var waiter = control.DelayTestTimeAsync(1, server.Token);
            Require(!waiter.IsCompleted, "The paused test delay did not wait.");
            server.Response = "{\"status\":\"running\",\"running\":true}";
            await control.RefreshPauseStateAsync(server.Token);
            await waiter.WaitAsync(server.Token);
            Require(!control.Snapshot().Paused, "A running response retained a stale paused flag.");
        }));
    }

    private static XemuControlManager NewControl(Process process, int port)
    {
        var control = new XemuControlManager(new XemuControlOptions { InputProvider = "unavailable", ConnectTimeoutMs = 2000 });
        control.Begin(process, Path.GetTempPath(), port);
        return control;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    // Only the external emulator protocol is substituted. Production parsing,
    // pause state transitions and resume dispatch execute unchanged.
    private sealed class StatusServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly Task _serve;
        public string Response { get; set; }
        public bool Continued { get; private set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public CancellationToken Token => _deadline.Token;

        public StatusServer(string response)
        {
            Response = response;
            _listener.Start();
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(Token);
                    using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
                    await using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                    await writer.WriteLineAsync("{\"QMP\":{\"version\":{\"qemu\":{\"major\":10,\"minor\":0,\"micro\":0},\"package\":\"fixture\"},\"capabilities\":[]}}");
                    for (var index = 0; index < 2; index++)
                    {
                        using var request = JsonDocument.Parse(await reader.ReadLineAsync(Token)
                            ?? throw new IOException("Status fixture lost an owned protocol command."));
                        var command = request.RootElement.GetProperty("execute").GetString();
                        var id = request.RootElement.GetProperty("id").GetInt32();
                        if (index == 0 && command != "qmp_capabilities") throw new InvalidDataException("Missing QMP negotiation.");
                        if (index == 1 && command is not ("query-status" or "cont")) throw new InvalidDataException("Unexpected QMP state command.");
                        if (command == "cont") Continued = true;
                        var response = command == "query-status" ? Response : "{}";
                        await writer.WriteLineAsync("{\"return\":" + response + ",\"id\":" + id + "}");
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _deadline.Cancel();
            await _serve;
            _listener.Stop();
            _deadline.Dispose();
        }
    }
}
