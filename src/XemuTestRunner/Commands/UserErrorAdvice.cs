using System.Text.Json;

namespace XemuTestRunner.Commands;

public sealed record UserErrorAdvice(
    string Code,
    string Error,
    string Hint);

public static class UserErrorAdviceFactory
{
    public static UserErrorAdvice From(
        Exception exception,
        string? context = null)
    {
        var prefix = string.IsNullOrWhiteSpace(context)
            ? ""
            : context.Trim() + ": ";

        return exception switch
        {
            JsonException =>
                new(
                    "json_invalid",
                    prefix + exception.Message,
                    "Fix the JSON syntax/type error and retry. Use the checked-in example config/job files as a schema reference."),

            InvalidDataException =>
                new(
                    "request_invalid",
                    prefix + exception.Message,
                    "Correct the reported field/path/value, then retry. For a job package run 'xemu-test-runner validate <package> -c runner.json'."),

            FileNotFoundException =>
                new(
                    "file_not_found",
                    prefix + exception.Message,
                    "Verify the configured/package-relative path exists and that the complete package was staged before retrying."),

            DirectoryNotFoundException =>
                new(
                    "directory_not_found",
                    prefix + exception.Message,
                    "Create/correct the configured directory path and retry."),

            UnauthorizedAccessException =>
                new(
                    "access_denied",
                    prefix + exception.Message,
                    "Correct filesystem permissions/ownership so the runner can read/write the requested path."),

            UriFormatException =>
                new(
                    "url_invalid",
                    prefix + exception.Message,
                    "Use an absolute runner URL such as http://127.0.0.1:9368."),

            HttpRequestException =>
                new(
                    "runner_unreachable",
                    prefix + exception.Message,
                    "Verify the runner is started, the advertised/listen address and port are correct, and the host firewall permits the connection."),

            IOException =>
                new(
                    "io_error",
                    prefix + exception.Message,
                    "Check disk space, file locks, package staging state, and path permissions before retrying."),

            _ =>
                new(
                    "internal_error",
                    prefix + exception.Message,
                    "Inspect the exception details/logs and current run evidence. If reproducible, preserve the package/result and report the failure.")
        };
    }
}
