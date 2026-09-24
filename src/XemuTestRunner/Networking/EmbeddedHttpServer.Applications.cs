namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task<bool?> TryApplicationIdentityRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        const string prefix = "/api/v1/applications/";
        if (request.Method != "GET" || !request.Path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var encoded = request.Path[prefix.Length..];
        if (encoded.Length == 0 || encoded.Contains('/')) return null;
        await WriteAgentJsonAsync(stream, AgentJobs.ApplicationIdentity(Uri.UnescapeDataString(encoded)), cancellationToken: ct).ConfigureAwait(false);
        return false;
    }
}
