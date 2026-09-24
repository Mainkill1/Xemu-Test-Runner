using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>Exercise the same QMP fake used by process checks, with an idle probe held open deterministically.</summary>
internal static class FakeQmpShutdownChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("fake QMP lifetime expiry drains idle clients without a crash", () => Check(false, false)));
        checks.Add(("fake QMP quit drains a second idle client without a crash", () => Check(true, false)));
        checks.Add(("fake QMP shutdown does not suppress malformed protocol errors", () => Check(false, true)));
    }

    private static async Task Check(bool quit, bool malformed)
    {
        // The fake starts its listener synchronously before RunAsync first yields.
        // As in the process fixture, reserve a candidate ephemeral loopback port.
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var run = FakeXemuHost.RunAsync(["-qmp", $"tcp:127.0.0.1:{port}", "--fake-runtime-ms", "2000"]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var idleReader = new StreamReader(idle.GetStream(), new UTF8Encoding(false), false, 4096, leaveOpen: true);
        if (await idleReader.ReadLineAsync(deadline.Token) is not { } greeting || !greeting.Contains("QMP", StringComparison.Ordinal))
            throw new InvalidDataException("The idle probe did not receive a QMP greeting.");

        using var command = new TcpClient();
        if (quit || malformed)
        {
            await command.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            using var reader = new StreamReader(command.GetStream(), new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(command.GetStream(), new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            if (await reader.ReadLineAsync(deadline.Token) is null)
                throw new InvalidDataException("The command connection did not receive a greeting.");
            await writer.WriteLineAsync((malformed ? "not-json" : "{\"execute\":\"quit\",\"id\":1}").AsMemory(), deadline.Token);
        }

        try
        {
            var exit = await run.WaitAsync(deadline.Token);
            if (malformed) throw new InvalidDataException("A protocol failure was swallowed during expected lifetime cancellation.");
            if (exit != 0) throw new InvalidDataException("Normal fake QMP shutdown did not return zero.");
        }
        catch (JsonException) when (malformed)
        {
            // Only expected cancellation should be normalized. Other client
            // errors remain visible even while an idle connection is cancelled.
        }
    }
}
