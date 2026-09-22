using System.Text.Json.Serialization;

namespace XemuTestRunner.Networking;

public sealed record ApiErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("hint")] string Hint,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("help")] string Help,
    [property: JsonPropertyName("details")] object? Details = null);

public static class ApiHelpCatalog
{
    public static object Describe() => new
    {
        api = "xemu-test-runner",
        version = "v1",
        errorContract = new
        {
            error = "Human-readable explanation of what was wrong.",
            code = "Stable machine-readable error code.",
            hint = "Concrete instruction for correcting or recovering from the request.",
            status = "HTTP status code.",
            help = "/api/v1/help",
            details = "Optional route-specific context."
        },
        routes = new object[]
        {
            new
            {
                method = "GET",
                path = "/api/v1/status",
                purpose = "Current runner, queue, telemetry, workstation, and active-job status."
            },
            new
            {
                method = "GET",
                path = "/api/v1/queue",
                purpose = "Queue counts. QueueIssue in /api/v1/status includes a code, message, and recovery hint."
            },
            new
            {
                method = "POST",
                path = "/api/v1/input/press",
                purpose = "Send one logical Xbox controller button to an active, unpaused xemu.",
                body = new
                {
                    Button = "A",
                    DurationMs = 100
                }
            },
            new
            {
                method = "POST",
                path = "/api/v1/xemu/pause",
                purpose = "Pause the active xemu target. Empty body."
            },
            new
            {
                method = "POST",
                path = "/api/v1/xemu/resume",
                purpose = "Resume the active xemu target. Empty body."
            },
            new
            {
                method = "POST",
                path = "/api/v1/xemu/quit",
                purpose = "Request a controlled quit of the active or preserved xemu target. Empty body."
            },
            new
            {
                method = "POST",
                path = "/api/v1/diagnostics/run",
                purpose = "Run a configured or ad-hoc diagnostic against the active target.",
                bodyExamples = new object[]
                {
                    new { Id = "cpu-window" },
                    new
                    {
                        Recipe = new
                        {
                            Id = "registers",
                            Type = "monitor",
                            MonitorCommand = "info registers"
                        }
                    }
                }
            },
            new
            {
                method = "GET",
                path = "/api/v1/files/<relative-path>",
                purpose = "Download a file from the configured file root. Supports one HTTP byte range."
            },
            new
            {
                method = "PUT/POST",
                path = "/api/v1/files/<relative-path>",
                purpose = "Stream an upload with Content-Length. Sequential resume uses Content-Range. X-Content-SHA256 may require whole-file digest verification."
            },
            new
            {
                method = "GET",
                path = "/api/v1/runs/<run-id>",
                purpose = "Run result metadata plus artifact list."
            },
            new
            {
                method = "GET",
                path = "/api/v1/experiments/<experiment-id>",
                purpose = "Aggregate measurements from comparison-eligible attempts only."
            }
        },
        commonErrors = new object[]
        {
            new
            {
                code = "route_not_found",
                meaning = "The requested path/method is not supported.",
                action = "Check /api/v1/help and use the documented method/path."
            },
            new
            {
                code = "request_body_invalid",
                meaning = "JSON/body fields are missing, malformed, or outside allowed ranges.",
                action = "Correct the body using the route example returned in the hint/details."
            },
            new
            {
                code = "target_not_active",
                meaning = "The request requires an active xemu target.",
                action = "Start or preserve a test target before retrying."
            },
            new
            {
                code = "target_not_ready",
                meaning = "The target exists but is paused or otherwise not ready for this operation.",
                action = "Resume/wait for the required state, then retry."
            },
            new
            {
                code = "operation_blocked",
                meaning = "The active job operation policy intentionally blocks this action.",
                action = "Do not disturb the benchmark, or explicitly allow the operation in job.json and accept the comparison implications."
            },
            new
            {
                code = "package_invalid",
                meaning = "The queued package/job plan is invalid.",
                action = "Run xemu-test-runner validate <package> and correct the reported field/path."
            },
            new
            {
                code = "package_busy",
                meaning = "The package is still changing or another process has a conflicting file handle.",
                action = "Finish the copy/move first; preferably stage under a dot-prefixed .incoming-* directory and rename when complete."
            }
        }
    };
}
