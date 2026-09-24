using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private sealed class ApiResponseState
    {
        public string RequestId { get; } = Guid.NewGuid().ToString("N");
        public bool Started { get; set; }
        public string? Allow { get; set; }
    }
    private static readonly ConditionalWeakTable<Stream, ApiResponseState> ResponseStates = new();
    private static ApiResponseState ResponseState(Stream stream) => ResponseStates.GetValue(stream, _ => new ApiResponseState());
    private static bool ResponseHasStarted(Stream stream) => ResponseState(stream).Started;
    private static void BeginApiResponse(Stream stream)
    {
        ResponseStates.Remove(stream);
        ResponseStates.Add(stream, new ApiResponseState());
    }

    private static Task WriteInputErrorAsync(Stream stream, ApiInputException error, CancellationToken ct)
    {
        ResponseState(stream).Allow = error.Allow;
        return WriteApiErrorAsync(stream, error.Status, AgentReason(error.Status), error.Code,
            error.Message, error.Hint, false, ct, error.Details);
    }

    private static async Task WriteApiErrorAsync(Stream stream, int status, string reason, string code,
        string message, string hint, bool keepAlive, CancellationToken cancellationToken, object? details = null)
    {
        var state = ResponseState(stream);
        // A partial artifact response is closed by the caller. Never append a
        // second HTTP/JSON response and corrupt the purported artifact bytes.
        if (state.Started) throw new IOException("HTTP response already started; request " + state.RequestId);
        System.Diagnostics.Trace.TraceError($"HTTP {state.RequestId} {status} {code}: {message}");
        static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "...";
        var recovery = status is 429 or 503 ? "wait" : status is 409 or 412 || status >= 500 ? "inspect" : "correct";
        if (code is "job_io_error" or "agent_io_error")
        {
            message = "Stored data could not be accessed or published. The server retained diagnostic details.";
            details = null;
        }
        else if (code is "request_invalid" or "job_request_invalid" && details is null)
            message = "Request validation failed. Inspect the API contract and server diagnostic reference.";
        else if (code == "route_not_found")
        {
            message = "No API route matches this request.";
            details = null;
        }
        if (details is not null && JsonSerializer.SerializeToUtf8Bytes(details, AgentJson).Length > 768)
            details = new { omitted = true };
        object Envelope(string text, string help, object? fields) => new
        {
            ok = false, status, code = Clip(code, 96), error = text, hint = help,
            requestId = state.RequestId, recovery, help = "/api/v1/help", details = fields
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(Envelope(Clip(message, 256), Clip(hint, 192), details), AgentJson);
        if (body.Length > 4096)
            body = JsonSerializer.SerializeToUtf8Bytes(Envelope(Clip(message, 96), Clip(hint, 96), new { omitted = true }), AgentJson);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store"
        };
        if (status == 405 && state.Allow is not null) headers["Allow"] = state.Allow;
        if (status is 429 or 503) headers["Retry-After"] = "1";
        await WriteHeadersAsync(stream, status, reason, headers, keepAlive, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
