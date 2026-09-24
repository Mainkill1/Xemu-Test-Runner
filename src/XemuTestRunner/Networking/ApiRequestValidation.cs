using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;

namespace XemuTestRunner.Networking;

internal sealed class ApiInputException(int status, string code, string message, string hint,
    object? details = null, string? allow = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Hint { get; } = hint;
    public object? Details { get; } = details;
    public string? Allow { get; } = allow;
}

/// <summary>Validate the wire request before any durable mutation. Storage JSON keeps its existing contract.</summary>
internal static class ApiRequestValidation
{
    private static readonly JsonSerializerOptions InputJson = new(ConfigLoader.JsonOptions)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    // The existing lower-level from-test contract reports an omitted revision as
    // 428, not a generic missing-constructor-parameter error. Other required IDs
    // are still checked by the store; a nullable revision never means "latest".
    private static readonly JsonSerializerOptions FromTestJson = new(InputJson)
    {
        RespectRequiredConstructorParameters = false
    };

    public static void Validate(HttpRequest request)
    {
        var allowed = AllowedMethods(request.Path);
        if (allowed is not null && !allowed.Split(", ").Contains(request.Method, StringComparer.Ordinal))
            throw new ApiInputException(405, "method_not_allowed", "This route does not accept that HTTP method.",
                "Use an allowed method; this request did not change stored state.", allow: allowed);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Uri.UnescapeDataString(pair.Split('=', 2)[0]);
            if (!names.Add(name))
                throw new ApiInputException(400, "query_duplicate", "A query parameter was supplied more than once.",
                    "Supply each query parameter once.");
        }
        if (request.Method is "GET" or "HEAD" || !request.Headers.TryGetValue("Origin", out var origin)) return;
        // This listener serves direct HTTP. Do not trust arbitrary forwarded
        // headers to upgrade an untrusted browser origin into a local one.
        if (!request.Headers.TryGetValue("Host", out var host) ||
            !Uri.TryCreate("http://" + host, UriKind.Absolute, out var target) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var source) ||
            target.UserInfo.Length != 0 || source.UserInfo.Length != 0 ||
            target.AbsolutePath != "/" || source.AbsolutePath != "/" ||
            target.Query.Length != 0 || target.Fragment.Length != 0 ||
            source.Query.Length != 0 || source.Fragment.Length != 0 ||
            !string.Equals(source.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) || source.Port != target.Port)
            throw new ApiInputException(403, "origin_forbidden", "Browser mutations require the runner's own origin.",
                "Open the workbench directly on this runner. Script clients may omit Origin; this is not authentication.");
    }

    public static void RequireJson(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Content-Type", out var value) ||
            !MediaTypeHeaderValue.TryParse(value, out var media) ||
            !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (media.CharSet is not null && !string.Equals(media.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new ApiInputException(415, "json_media_type_required", "This request requires UTF-8 application/json.",
                "Send Content-Type: application/json with a UTF-8 JSON object.");
    }

    public static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A JSON object is required.", "$", null, null);
            RejectDuplicates(document.RootElement, "$", typeof(T));
            return JsonSerializer.Deserialize<T>(bytes, typeof(T) == typeof(AgentTestRunRequest) ? FromTestJson : InputJson)
                ?? throw new JsonException("A JSON object is required.", "$", null, null);
        }
        catch (JsonException error)
        {
            throw new ApiInputException(400, "request_invalid", "JSON contains a missing, unknown, duplicate, null or incorrectly typed field.",
                "Correct the named field against the saved configuration or API contract. No request was saved or started.",
                new { field = Clip(error.Path ?? "$", 160) });
        }
    }

    private static void RejectDuplicates(JsonElement value, string path, Type type)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Model properties are case-insensitive; dictionary keys (for example
            // Linux environment variables) remain case-sensitive data.
            var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType &&
                item.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            var readOnlyDictionary = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType &&
                item.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
            var valueType = (dictionary ?? readOnlyDictionary)?.GetGenericArguments()[1];
            var names = new HashSet<string>(valueType is null ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var next = path + "." + property.Name;
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON field.", next, null, null);
                var member = type.GetProperties().FirstOrDefault(item => string.Equals(
                    item.GetCustomAttributes(typeof(JsonPropertyNameAttribute), true).OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name ?? item.Name,
                    property.Name, StringComparison.OrdinalIgnoreCase));
                RejectDuplicates(property.Value, next, valueType ?? member?.PropertyType ?? typeof(JsonElement));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var elementType = type.IsArray ? type.GetElementType() : type.GetInterfaces().Append(type)
                .FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            var index = 0;
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item, path + "[" + index++ + "]", elementType ?? typeof(JsonElement));
        }
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];

    internal static string? AllowedMethods(string path)
    {
        if (path is "/tests" or "/api/v1/builds" or "/api/v1/compare" or "/api/v1/test-configs" or
            "/api/v1/tests" or "/api/v1/help" or "/api/v1/agent" or "/api/v1/openapi.json" or "/api/v1" or "/.well-known/agent.json") return "GET";
        if (path == "/api/v1/baseline") return "GET, PUT";
        if (path == "/api/v1/build-results/index") return "POST";
        if (path.StartsWith("/api/v1/build-results/", StringComparison.Ordinal)) return "GET";
        if (path is "/api/v1/test-runs" or "/api/v1/jobs") return "GET, POST";
        if (path == "/api/v1/jobs/from-test") return "POST";
        if (path.StartsWith("/api/v1/test-configs/", StringComparison.Ordinal))
        {
            var parts = path[21..].Split('/');
            return parts.Length == 1 ? "POST" : parts.Length == 2 ? "GET" : null;
        }
        if (path.StartsWith("/api/v1/test-runs/", StringComparison.Ordinal))
        {
            var parts = path[18..].Split('/');
            return parts.Length == 1 ? "GET, DELETE" : parts.Length == 2 && parts[1] == "start" ? "POST" : null;
        }
        if (path.StartsWith("/api/v1/jobs/", StringComparison.Ordinal))
        {
            var parts = path[13..].Split('/', 3);
            if (parts.Length == 1) return "GET, DELETE";
            if (parts.Length == 3 && parts[1] == "files") return "GET, HEAD, PUT, POST";
            if (parts.Length == 2) return parts[1] switch
            {
                "plan" => "PUT", "files" or "operation" or "validation" => "GET",
                "submit" or "validate" or "withdraw" or "clone" or "reuse" => "POST", _ => null
            };
        }
        return null;
    }
}
