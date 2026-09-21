using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class FakeXemuHost
{
    private static readonly byte[] PngFixture =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4,
        0x89, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x44, 0x41,
        0x54, 0x08, 0xd7, 0x63, 0xf8, 0xcf, 0xc0, 0xf0,
        0x1f, 0x00, 0x05, 0x00, 0x01, 0xff, 0x89, 0x99,
        0x3d, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
        0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
    ];

    public static void WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, PngFixture);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var qmp = ValueAfter(args, "-qmp")
            ?? throw new InvalidDataException("The fake xemu requires the runner-owned -qmp argument.");
        var endpoint = ParseEndpoint(qmp);
        var runtimeMs = int.Parse(ValueAfter(args, "--fake-runtime-ms") ?? "1000");
        var paused = args.Contains("-S", StringComparer.Ordinal);

        using var lifetime = new CancellationTokenSource(runtimeMs);
        var listener = new TcpListener(endpoint.Address, endpoint.Port);
        listener.Start();
        Console.WriteLine($"fake-xemu QMP ready on port {endpoint.Port}");

        var clients = new List<Task>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                clients.Add(HandleClientAsync(
                    client,
                    () => paused,
                    value => paused = value,
                    () => lifetime.CancelAfter(50),
                    lifetime.Token));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
        return 0;
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        Func<bool> paused,
        Action<bool> setPaused,
        Action requestQuit,
        CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        })
        {
            await writer.WriteLineAsync("{\"QMP\":{\"version\":{},\"capabilities\":[]}}".AsMemory(), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    return;

                using var request = JsonDocument.Parse(line);
                var root = request.RootElement;
                var id = root.GetProperty("id").GetInt32();
                var command = root.GetProperty("execute").GetString();

                // Real xemu closes the QMP socket while handling quit instead of
                // sending a command response. Keep the fixture faithful to that
                // shutdown behavior so the runner cannot mistake it for failure.
                if (command == "quit")
                {
                    requestQuit();
                    return;
                }

                if (command == "screendump")
                {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(new
                        {
                            error = new { @class = "CommandNotFound", desc = "The command screendump has not been found" },
                            id
                        }).AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                object response = command switch
                {
                    "qmp_capabilities" => new { },
                    "query-status" => new { status = paused() ? "paused" : "running" },
                    "stop" => SetPause(true),
                    "cont" => SetPause(false),
                    _ => new { }
                };
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(new { @return = response, id }).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        object SetPause(bool value)
        {
            setPaused(value);
            return new { };
        }

    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i].Equals(name, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static IPEndPoint ParseEndpoint(string qmp)
    {
        var value = qmp.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? qmp[4..] : qmp;
        var hostAndPort = value.Split(',', 2)[0];
        var separator = hostAndPort.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(hostAndPort[(separator + 1)..], out var port))
            throw new InvalidDataException("Invalid fake QMP endpoint: " + qmp);
        var host = hostAndPort[..separator];
        var address = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(address, port);
    }
}
