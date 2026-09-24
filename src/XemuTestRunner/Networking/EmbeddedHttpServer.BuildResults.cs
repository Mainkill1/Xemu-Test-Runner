using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record IndexBuildRequest(string RunId);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PinBaselineRequest(string Sha256);

    private async Task<bool?> TryBuildResultRoutesAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Path == "/api/v1/help" && request.Method == "GET" && GetQueryValue(request.Query, "topic") == "build-results")
        {
            await WriteAgentJsonAsync(stream, new
            {
                capabilities = new[] { "executableHashResults", "pinnedBaseline", "serverComparison", "compactReports" },
                result = "/api/v1/build-results/{sha256}", comparison = "/api/v1/compare?A={sha256}&B={sha256}",
                baseline = "/api/v1/baseline", formats = new[] { "json", "markdown", "csv (comparison only)" },
                rule = "A omitted uses the explicitly pinned baseline. No first/latest baseline is selected automatically. Indexing reads archived evidence, never starts tests.",
                index = "POST /api/v1/build-results/index {runId}", indexStatus = "/api/v1/build-results/index-status"
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        var supported = request.Path == "/api/v1/baseline" || request.Path == "/api/v1/compare" || request.Path.StartsWith("/api/v1/build-results/", StringComparison.Ordinal);
        if (!supported) return null;
        // This is explicit result analysis, not the constant-cost status path.
        // It reads bounded summary metadata only, never raw measurement CSVs.
        if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
        if (request.Path == "/api/v1/baseline")
        {
            if (request.Method == "GET")
            {
                await WriteAgentJsonAsync(stream, AgentJobs.BuildResults.Baseline(), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (request.Method == "PUT")
            {
                var body = await ReadAgentBodyAsync<PinBaselineRequest>(stream, request, ct).ConfigureAwait(false);
                await WriteAgentJsonAsync(stream, AgentJobs.BuildResults.Pin(body.Sha256), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        if (request.Path == "/api/v1/build-results/index" && request.Method == "POST")
        {
            var body = await ReadAgentBodyAsync<IndexBuildRequest>(stream, request, ct).ConfigureAwait(false);
            var value = AgentJobs.IndexBuildResult(body.RunId);
            await WriteAgentJsonAsync(stream, new { value.RunId, value.Sha256, value.Eligible, value.Issues }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/api/v1/build-results/index-status" && request.Method == "GET")
        {
            await WriteAgentJsonAsync(stream, AgentJobs.BuildIndexStatus(), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/api/v1/compare" && request.Method == "GET")
        {
            var format = GetQueryValue(request.Query, "format") ?? "json";
            if (format is not ("json" or "markdown" or "csv")) throw new InvalidDataException("format must be json, markdown or csv.");
            var a = GetQueryValue(request.Query, "A");
            var b = GetQueryValue(request.Query, "B");
            _ = BuildResultStore.Sha(b);
            if (a is not null) _ = BuildResultStore.Sha(a);
            var result = AgentJobs.BuildResults.Compare(a, b!, format == "csv");
            if (format == "json") await WriteAgentJsonAsync(stream, result, cancellationToken: ct).ConfigureAwait(false);
            else await WriteBuildTextAsync(stream, format == "csv" ? BuildResultFormatting.Csv(result) : BuildResultFormatting.Markdown(result), format, ct).ConfigureAwait(false);
            return false;
        }
        const string prefix = "/api/v1/build-results/";
        if (request.Path.StartsWith(prefix, StringComparison.Ordinal) && request.Method == "GET")
        {
            var parts = request.Path[prefix.Length..].Split('/');
            var sha = BuildResultStore.Sha(parts[0]);
            if (parts.Length == 2 && parts[1] == "runs")
            {
                var offset = ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000);
                var limit = ReadBoundedQuery(request.Query, "limit", 20, 1, 100);
                var values = AgentJobs.BuildResults.Runs(sha);
                await WriteAgentJsonAsync(stream, new { items = values.Skip(offset).Take(limit).ToArray(), nextOffset = offset + limit < values.Count ? (int?)(offset + limit) : null }, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 1)
            {
                var format = GetQueryValue(request.Query, "format") ?? "json";
                if (format is not ("json" or "markdown")) throw new InvalidDataException("Build summary format must be json or markdown. Raw CSV is linked per run.");
                var result = AgentJobs.BuildResults.Summary(sha);
                if (format == "json") await WriteAgentJsonAsync(stream, result, cancellationToken: ct).ConfigureAwait(false);
                else await WriteBuildTextAsync(stream, BuildResultFormatting.Markdown(result), format, ct).ConfigureAwait(false);
                return false;
            }
        }
        throw new AgentRequestException(404, "route_not_found", "No matching build-results action.", "Read /api/v1/help?topic=build-results.");
    }

    private static async Task WriteBuildTextAsync(Stream stream, string text, string format, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (format != "csv" && bytes.Length > 8192) throw new InvalidDataException("Formatted result exceeds the report budget.");
        var headers = new Dictionary<string, string> {
            ["Content-Type"] = format == "csv" ? "text/csv; charset=utf-8" : "text/markdown; charset=utf-8",
            ["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture), ["Cache-Control"] = "no-store"
        };
        if (format == "csv") headers["Content-Disposition"] = "attachment; filename=\"comparison.csv\"";
        await WriteHeadersAsync(stream, 200, "OK", headers, false, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
