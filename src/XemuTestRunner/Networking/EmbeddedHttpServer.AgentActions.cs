using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    // These actions have no parameters. Consume the commonly sent empty JSON
    // object before writing a successful response and closing the connection;
    // leaving inbound bytes unread can turn that close into a TCP reset.
    private static async Task ConsumeAgentActionBodyAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        const string prefix = "/api/v1/jobs/";
        if (request.Method != "POST" || !request.Path.StartsWith(prefix, StringComparison.Ordinal)) return;
        var parts = request.Path[prefix.Length..].Split('/');
        if (parts.Length != 2 || parts[1] is not ("submit" or "validate" or "withdraw")) return;
        var length = request.ContentLength.GetValueOrDefault();
        if (length == 0) return;
        if (length is < 0 or > 4096)
            throw new AgentRequestException(400, "action_body_invalid", "This action accepts no body or an empty JSON object up to 4096 bytes.", "Remove action parameters; edit the draft plan through its plan endpoint.");
        var bytes = new byte[(int)length];
        await ReadExactlyAsync(stream, bytes, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
            throw new AgentRequestException(400, "action_body_invalid", "This action has no body parameters.", "Send no body or {}. No action was performed.");
    }
}
