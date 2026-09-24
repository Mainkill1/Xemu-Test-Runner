using System.Globalization;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private static readonly JsonSerializerOptions AgentJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly object _agentStoreGate = new();
    private AgentJobStore? _agentStore;
    private AgentJobStore AgentJobs
    {
        get
        {
            lock (_agentStoreGate)
            {
                return _agentStore ??= new AgentJobStore(_paths, _uploads, Reliability.Preflight, _options.TransferBufferBytes)
                {
                    Activity = Activity
                };
            }
        }
    }

    private async Task<bool?> TryAgentRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method == "GET" && request.Path is ("/api/v1" or "/api/v1/agent" or "/.well-known/agent.json"))
        {
            await WriteAgentJsonAsync(stream, AgentApiCatalog.Describe(), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Method == "GET" && request.Path == "/api/v1/openapi.json")
        {
            await WriteAgentJsonAsync(stream, AgentApiCatalog.OpenApi(), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Method == "GET" && request.Path == "/api/v1/help")
        {
            await WriteAgentJsonAsync(stream, new { agent = AgentApiCatalog.Describe(), controls = ApiHelpCatalog.Describe() }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        const string prefix = "/api/v1/jobs";
        if (request.Path != prefix && !request.Path.StartsWith(prefix + "/", StringComparison.Ordinal)) return null;
        var responseStarted = false;
        try
        {
            // Rejected mutations close the connection, so unread bodies cannot
            // be interpreted as a second HTTP request on the same connection.
            if (request.Method is not ("GET" or "HEAD"))
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
            }

            var suffix = request.Path[prefix.Length..].TrimStart('/');
            if (suffix.Length == 0)
            {
                if (request.Method == "POST")
                {
                    var body = await ReadAgentBodyAsync<AgentJobRequest>(stream, request, ct).ConfigureAwait(false);
                    var result = AgentJobs.Create(body);
                    await WriteAgentJsonAsync(stream, result, location: AgentJobStore.Url(result.Id), revision: result.Revision, cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
                if (request.Method == "GET")
                {
                    var offset = ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000);
                    var limit = ReadBoundedQuery(request.Query, "limit", 25, 1, 100);
                    await WriteAgentJsonAsync(stream, AgentJobs.List(offset, limit), cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
            }

            var parts = suffix.Split('/', 3);
            var id = Uri.UnescapeDataString(parts[0]);
            var action = parts.Length > 1 ? parts[1] : "";
            if (action == "" && request.Method is ("GET" or "DELETE"))
            {
                var job = request.Method == "DELETE" ? AgentJobs.Cancel(id) : AgentJobs.Get(id);
                await WriteAgentJsonAsync(stream, job, revision: job.Revision, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && action == "plan" && request.Method == "PUT")
            {
                var plan = await ReadAgentBodyAsync<JobDefinition>(stream, request, ct).ConfigureAwait(false);
                var job = AgentJobs.ReplacePlan(id, plan, request.Headers.GetValueOrDefault("If-Match"));
                await WriteAgentJsonAsync(stream, job, revision: job.Revision, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && request.Method == "GET")
            {
                object? result = action switch
                {
                    "files" => AgentJobs.Files(id),
                    "operation" => AgentJobs.GetOperation(id),
                    "validation" => AgentJobs.Validation(id),
                    _ => null
                };
                if (action is "files" or "operation" or "validation")
                {
                    await WriteAgentJsonAsync(stream, result, cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
            }
            if (parts.Length == 2 && request.Method == "POST")
            {
                if (action == "withdraw")
                {
                    var job = AgentJobs.Withdraw(id);
                    await WriteAgentJsonAsync(stream, job, revision: job.Revision, cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
                if (action is "submit" or "validate")
                {
                    var operation = AgentJobs.StartValidation(id, action == "submit", ct);
                    await WriteAgentJsonAsync(stream, operation, 202, AgentJobStore.Url(id) + "/operation", cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
                if (action == "clone")
                {
                    var clone = await ReadAgentBodyAsync<CloneJobRequest>(stream, request, ct).ConfigureAwait(false);
                    var operation = AgentJobs.Clone(id, clone.NewId, ct);
                    await WriteAgentJsonAsync(stream, operation, 202, AgentJobStore.Url(operation.JobId) + "/operation", cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
            }
            if (parts.Length == 3 && action == "files")
            {
                var relative = Uri.UnescapeDataString(parts[2]);
                if (request.Method == "GET" && QueryContains(request.Query, "upload-status", "1"))
                {
                    await WriteAgentJsonAsync(stream, AgentJobs.FileStatus(id, relative), cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
                if (request.Method is "PUT" or "POST")
                {
                    var upload = ParseUploadRequest(request);
                    using var transfer = Activity.TrackTransfer(new { apiJob = id, file = relative, action = "upload" });
                    var receipt = await AgentJobs.UploadAsync(id, relative, stream, upload.Length, upload.Range,
                        upload.ExpectedHash, upload.Id, ct).ConfigureAwait(false);
                    await WriteAgentJsonAsync(stream, receipt, receipt.Complete ? 201 : 202, cancellationToken: ct).ConfigureAwait(false);
                    return false;
                }
                if (request.Method is "GET" or "HEAD")
                {
                    if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                    await using var file = AgentJobs.OpenFile(id, relative);
                    FileRange range;
                    try { range = FileRange.Parse(request.Headers.GetValueOrDefault("Range"), file.Length); }
                    catch (InvalidDataException)
                    {
                        responseStarted = true;
                        await WriteHeadersAsync(stream, 416, "Range Not Satisfiable", new Dictionary<string, string>
                        {
                            ["Content-Range"] = $"bytes */{file.Length}", ["Content-Length"] = "0"
                        }, false, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        return false;
                    }
                    var headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/octet-stream",
                        ["Content-Length"] = range.Length.ToString(CultureInfo.InvariantCulture),
                        ["Accept-Ranges"] = "bytes", ["Cache-Control"] = "no-store"
                    };
                    if (range.Partial) headers["Content-Range"] = $"bytes {range.Start}-{range.Start + range.Length - 1}/{file.Length}";
                    using var transfer = Activity.TrackTransfer(new { apiJob = id, file = relative, action = "download" });
                    responseStarted = true;
                    await WriteHeadersAsync(stream, range.Partial ? 206 : 200, range.Partial ? "Partial Content" : "OK", headers, false, ct).ConfigureAwait(false);
                    if (request.Method == "GET")
                    {
                        file.Position = range.Start;
                        await CopyBytesAsync(file, stream, range.Length, ct).ConfigureAwait(false);
                    }
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    return false;
                }
            }
            throw new AgentRequestException(404, "route_not_found", "No matching job action.", "GET /api/v1/agent and follow the returned job actions.");
        }
        catch (Exception) when (responseStarted) { throw; }
        catch (AgentRequestException exception)
        {
            await WriteApiErrorAsync(stream, exception.Status, AgentReason(exception.Status), exception.Code,
                exception.Message, exception.Hint, false, ct).ConfigureAwait(false);
        }
        catch (UploadFailure exception)
        {
            await WriteApiErrorAsync(stream, exception.Status, AgentReason(exception.Status), exception.Code,
                exception.Message, exception.Hint, false, ct, exception.Details).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "job_request_invalid", exception.Message,
                "Use the manifest and plan formats in GET /api/v1/agent. Correct the request before retrying.", false, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "job_io_error", exception.Message,
                "GET the job and upload status. Check free space/permissions; do not create a second attempt merely because a request failed.", false, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static int ReadBoundedQuery(string query, string key, int fallback, int minimum, int maximum)
    {
        var value = GetQueryValue(query, key);
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
            throw new InvalidDataException($"{key} must be between {minimum} and {maximum}.");
        return number;
    }

    private static async Task<T> ReadAgentBodyAsync<T>(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is not long length || length is < 1 or > 1048576)
            throw new InvalidDataException("This JSON request requires Content-Length between 1 and 1048576 bytes.");
        var bytes = new byte[(int)length];
        await ReadExactlyAsync(stream, bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("The JSON body cannot be null.");
    }

    private static async Task WriteAgentJsonAsync(Stream stream, object? value, int status = 200,
        string? location = null, string? revision = null, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AgentJson);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store", ["Link"] = "</api/v1/agent>; rel=\"service-desc\""
        };
        if (location is not null) headers["Location"] = location;
        if (revision is not null) headers["ETag"] = revision;
        if (status == 202) headers["Retry-After"] = "1";
        await WriteHeadersAsync(stream, status, AgentReason(status), headers, false, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string AgentReason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 400 => "Bad Request", 404 => "Not Found",
        409 => "Conflict", 411 => "Length Required", 412 => "Precondition Failed",
        422 => "Unprocessable Content", 428 => "Precondition Required", _ => "Error"
    };
    private sealed record CloneJobRequest(string NewId);
}
