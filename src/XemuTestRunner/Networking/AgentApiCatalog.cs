namespace XemuTestRunner.Networking;

/// <summary>One route catalog feeds both agent discovery and the workflow OpenAPI document.</summary>
internal static class AgentApiCatalog
{
    private sealed record Route(string Method, string Path, string Id, string Purpose, string? Body = null);
    private static readonly Route[] Routes =
    [
        new("GET", "/api/v1/agent", "discoverAgentWorkflow", "Start here. Read the no-SSH workflow, limits and existing control/result links."),
        new("GET", "/api/v1/jobs", "listAgentJobs", "List API-owned jobs using offset and limit (maximum 100)."),
        new("POST", "/api/v1/jobs", "createJobDraft", "Create an editable draft. Repeating the same ID and creation document is safe; different content conflicts.", "create"),
        new("GET", "/api/v1/jobs/{id}", "getAgentJob", "Read state, revision, plan, current run link and legal next actions."),
        new("DELETE", "/api/v1/jobs/{id}", "cancelUnstartedJob", "Cancel a draft or unclaimed queued job without deleting its files. Active/completed attempts are immutable."),
        new("PUT", "/api/v1/jobs/{id}/plan", "replaceDraftPlan", "Replace the draft plan using its quoted revision as If-Match. Job.Id stays unchanged.", "plan"),
        new("GET", "/api/v1/jobs/{id}/files", "listPackageFiles", "Read per-file declarations, completion state, committed offsets and upload URLs."),
        new("GET", "/api/v1/jobs/{id}/files/{path}", "getPackageFile", "Download the file with Range, or query ?upload-status=1 before resuming an upload."),
        new("HEAD", "/api/v1/jobs/{id}/files/{path}", "inspectPackageFile", "Read Content-Length and byte-range support without downloading the body."),
        new("PUT", "/api/v1/jobs/{id}/files/{path}", "uploadPackageFile", "Stream a declared file. Content-Length is mandatory; sequential Content-Range and X-Upload-Id resume supported.", "binary"),
        new("POST", "/api/v1/jobs/{id}/validate", "validatePackage", "Start hash verification/preflight. Returns 202 plus operation Location; does not start xemu."),
        new("GET", "/api/v1/jobs/{id}/validation", "getPackageValidation", "Read the last structured preflight report."),
        new("POST", "/api/v1/jobs/{id}/submit", "submitPackage", "Validate then atomically publish the complete package. Repeated submit does not create a second attempt."),
        new("GET", "/api/v1/jobs/{id}/operation", "getJobOperation", "Poll validation, submission or cloning. queued/running are nonterminal; completed/failed/interrupted are terminal."),
        new("POST", "/api/v1/jobs/{id}/withdraw", "withdrawQueuedJob", "Atomically return an unclaimed queued package to draft. A queue claim winning the race returns conflict."),
        new("POST", "/api/v1/jobs/{id}/clone", "cloneJobDraft", "Copy a stable draft/completed/cancelled package on the tester into a new ID; returns an operation for the new draft.", "clone")
    ];

    public static object Describe() => new
    {
        name = "Xemu Test Runner agent workflow",
        version = "v1",
        transport = "trusted-LAN HTTP; no shell login required after the foreground runner is started",
        openapi = "/api/v1/openapi.json",
        workflow = new[]
        {
            "Create a draft with a stable client-chosen Id, Job and Files. Files contain Path, Length, Sha256 and optional Executable.",
            "Upload the declared payload files to the returned URLs. For large files use chunks; query committed upload status after any disconnect.",
            "Edit Job with PUT plan and If-Match if needed. job.json itself cannot be uploaded as a payload file.",
            "POST submit, then follow Location until the operation completes. A 202 means accepted, not validated or passed.",
            "Poll the job until it returns a run action; follow that URL for assessment and artifact links. Use /api/v1/metrics/latest for fast cached telemetry.",
            "Download immutable result artifacts through /api/v1/runs/{runId}/artifacts/{path}. Use tails for live inspection where policy permits.",
            "For another attempt, clone to a NEW ID, edit the new draft, then submit. Never rewrite a running plan or completed evidence."
        },
        limits = new
        {
            jsonBodyBytes = 1048576, declaredFiles = 4096, listPageSize = 100,
            uploadLength = "signed 64-bit bytes; no 10 GB application cap",
            parallelWrites = "one mutation per API job; different draft jobs may upload independently",
            suggestedUploadChunkBytes = 8388608,
            suggestedOperationPollMs = 1000,
            fastPolling = "Only status/metrics use cached sampling; do not hash, list payloads or poll discovery at 100 ms."
        },
        requestExample = new
        {
            Id = "build-a-001",
            Job = new { Id = "build-a-001", Executable = "xemu.exe", TargetOs = "windows", Arguments = new[] { "-config_path", "xemu.toml" }, RequiredFiles = new[] { "xemu.toml" }, TimeoutSeconds = 120 },
            Files = new object[]
            {
                new { Path = "xemu.exe", Length = 123L, Sha256 = "replace-with-real-64-hex-sha256", Executable = true },
                new { Path = "xemu.toml", Length = 456L, Sha256 = "replace-with-real-64-hex-sha256" }
            }
        },
        routes = Routes,
        existingApis = new
        {
            status = "/api/v1/status", metrics = "/api/v1/metrics/latest", queue = "/api/v1/queue",
            runs = "/api/v1/runs", control = "/api/v1/control", diagnostics = "/api/v1/diagnostics",
            pause = new AgentAction("POST", "/api/v1/xemu/pause", "Inspection only where the active operation policy allows it."),
            resume = new AgentAction("POST", "/api/v1/xemu/resume", "Resume the controlled VM."),
            quit = new AgentAction("POST", "/api/v1/xemu/quit", "Request target shutdown; not a correctness assertion."),
            input = new AgentAction("POST", "/api/v1/input/press", "Send a logical controller button."),
            screenshot = "/api/v1/screenshot"
        },
        errors = "Existing error/code/hint/status/help/details contract. 409 job_busy: poll; 412/428: refresh revision; 422: repair payload/plan. Do not fall back to SSH.",
        boundaries = new[]
        {
            "Draft edits and unclaimed withdrawals are supported. Active execution plans and completed evidence are not editable.",
            "Job APIs manage their own agent-* packages; existing manually staged packages remain on the legacy queue APIs.",
            "HTTP is deliberately LAN-only and unauthenticated. HTTPS/service installation/remote app restart are not added by this workflow.",
            "The runner must stay running: do not launch it with --once or --one-shot for remote queue operation.",
            "Benchmark policy may reject uploads, downloads, cloning or validation. Respect that response rather than using a shell to bypass it."
        }
    };

    public static object OpenApi()
    {
        var paths = new Dictionary<string, Dictionary<string, object>>();
        foreach (var route in Routes)
        {
            if (!paths.TryGetValue(route.Path, out var methods)) paths[route.Path] = methods = new();
            var parameters = new List<object>();
            if (route.Path.Contains("{id}")) parameters.Add(Parameter("id", "path", true, "Stable API job ID", new { type = "string", pattern = "^[a-z0-9][a-z0-9-]{0,63}$" }));
            if (route.Path.Contains("{path}")) parameters.Add(Parameter("path", "path", true, "Declared relative file path; encode its individual components", new { type = "string" }));
            if (route.Path == "/api/v1/jobs" && route.Method == "GET")
            {
                parameters.Add(Parameter("offset", "query", false, "Pagination offset", new { type = "integer", minimum = 0 }));
                parameters.Add(Parameter("limit", "query", false, "Page size", new { type = "integer", minimum = 1, maximum = 100 }));
            }
            if (route.Body == "plan") parameters.Add(Parameter("If-Match", "header", true, "Quoted revision from GET job", new { type = "string" }));
            if (route.Path.Contains("{path}"))
            {
                if (route.Method == "GET") parameters.Add(Parameter("upload-status", "query", false, "Set to 1 to inspect the committed upload offset", new { type = "integer", @enum = new[] { 1 } }));
                parameters.Add(Parameter(route.Body == "binary" ? "Content-Range" : "Range", "header", false, "One sequential upload range or one download byte range", new { type = "string" }));
            }
            if (route.Body == "binary")
            {
                parameters.Add(Parameter("Content-Length", "header", true, "Bytes in this request body", new { type = "integer", format = "int64", minimum = 0 }));
                parameters.Add(Parameter("X-Upload-Id", "header", false, "Resume identity returned by upload status", new { type = "string" }));
                parameters.Add(Parameter("X-Content-SHA256", "header", false, "Whole-file digest; must match Files declaration", new { type = "string" }));
            }
            var operation = new Dictionary<string, object>
            {
                ["operationId"] = route.Id,
                ["summary"] = route.Purpose,
                ["parameters"] = parameters,
                ["responses"] = new Dictionary<string, object>
                {
                    ["200"] = new { description = "Current resource or idempotent creation result" },
                    ["201"] = new { description = "Complete verified upload" },
                    ["202"] = new { description = "Operation accepted or upload incomplete; follow Location/status, not a test verdict" },
                    ["default"] = new { description = "JSON error with code, hint, status, help and optional details" }
                }
            };
            if (route.Body is not null)
            {
                object schema = route.Body switch
                {
                    "binary" => new { type = "string", format = "binary" },
                    "clone" => new { type = "object", required = new[] { "newId" }, properties = new { newId = new { type = "string" } } },
                    "plan" => new { type = "object", description = "Full JobDefinition document, same format as job.json. Id must remain the API job ID." },
                    _ => new
                    {
                        type = "object", required = new[] { "id", "job", "files" },
                        properties = new
                        {
                            id = new { type = "string" }, job = new { type = "object", description = "Existing job.json schema" },
                            files = new { type = "array", minItems = 1, maxItems = 4096, items = new
                            {
                                type = "object", required = new[] { "path", "length", "sha256" }, properties = new
                                {
                                    path = new { type = "string" }, length = new { type = "integer", format = "int64", minimum = 0 },
                                    sha256 = new { type = "string", pattern = "^[0-9a-fA-F]{64}$" }, executable = new { type = "boolean" }
                                }
                            } }
                        }
                    }
                };
                operation["requestBody"] = new { required = true, content = new Dictionary<string, object>
                { [route.Body == "binary" ? "application/octet-stream" : "application/json"] = new { schema } } };
            }
            methods[route.Method.ToLowerInvariant()] = operation;
        }
        return new { openapi = "3.1.0", info = new { title = "Xemu Test Runner agent package workflow", version = "1.0.0",
            description = "New package workflow only. Existing xemu controls/results are documented by GET /api/v1/help." },
            servers = new[] { new { url = "/" } }, paths };
    }

    private static object Parameter(string name, string location, bool required, string description, object schema) =>
        new Dictionary<string, object> { ["name"] = name, ["in"] = location, ["required"] = required, ["description"] = description, ["schema"] = schema };
}
