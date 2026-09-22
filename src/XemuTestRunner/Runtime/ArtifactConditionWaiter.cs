using System.Security.Cryptography;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public static class ArtifactConditionWaiter
{
    public static async Task WaitAsync(
        ArtifactCheckDefinition condition,
        int timeoutMs,
        int pollIntervalMs,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime,
        CancellationToken cancellationToken)
    {
        if (timeoutMs <= 0)
            throw new InvalidDataException(
                "wait_for_artifact TimeoutMs must be greater than zero.");
        if (pollIntervalMs is < 25 or > 5000)
            throw new InvalidDataException(
                "wait_for_artifact PollIntervalMs must be between 25 and 5000.");

        var path = Resolve(
            condition,
            packageDirectory,
            resultDirectory,
            runtime);

        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(timeoutMs);

        string? lastDetail = null;

        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();

            var result = await CheckAsync(
                condition,
                path,
                deadline.Token).ConfigureAwait(false);

            if (result.Passed)
                return;

            lastDetail = result.Detail;

            try
            {
                await Task.Delay(
                    pollIntervalMs,
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"wait_for_artifact timed out after {timeoutMs} ms for '{condition.Scope}:{condition.Path}'. Last state: {lastDetail}");
            }
        }
    }

    private static async Task<(bool Passed, string Detail)>
        CheckAsync(
            ArtifactCheckDefinition condition,
            string path,
            CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return (
                !condition.MustExist,
                condition.MustExist
                    ? "artifact does not exist"
                    : "optional artifact is absent");

        var info = new FileInfo(path);

        if (condition.MinimumBytes > 0 &&
            info.Length < condition.MinimumBytes)
            return (
                false,
                $"artifact has {info.Length} bytes; requires {condition.MinimumBytes}");

        if (!string.IsNullOrWhiteSpace(
                condition.ExpectedSha256))
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken)
                .ConfigureAwait(false))
                .ToLowerInvariant();

            if (!actual.Equals(
                    condition.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                return (
                    false,
                    $"SHA-256 is {actual}");
        }

        if (!string.IsNullOrEmpty(
                condition.ContainsText) ||
            condition.EqualsText is not null)
        {
            info.Refresh();
            if (info.Length > 16 * 1024 * 1024)
                return (
                    false,
                    "text condition source exceeds 16 MiB");

            string text;
            try
            {
                text = await File.ReadAllTextAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                return (
                    false,
                    "artifact is not readable yet: " +
                    ex.Message);
            }

            if (!string.IsNullOrEmpty(
                    condition.ContainsText) &&
                !text.Contains(
                    condition.ContainsText,
                    StringComparison.Ordinal))
                return (
                    false,
                    $"required text '{condition.ContainsText}' is not present");

            if (condition.EqualsText is not null &&
                !string.Equals(
                    text,
                    condition.EqualsText,
                    StringComparison.Ordinal))
                return (
                    false,
                    "artifact text does not exactly match");
        }

        return (true, "condition satisfied");
    }

    private static string Resolve(
        ArtifactCheckDefinition definition,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime) =>
        definition.Scope.Trim().ToLowerInvariant() switch
        {
            "package" =>
                JobDefinition.ResolveInsidePackage(
                    packageDirectory,
                    definition.Path),
            "result" =>
                ResolveInside(
                    resultDirectory,
                    definition.Path),
            "runtime" =>
                runtime is null
                    ? throw new InvalidDataException(
                        "wait_for_artifact requested runtime scope without RuntimeState.")
                    : RuntimeStateManager.ResolveInside(
                        runtime.Directory,
                        definition.Path),
            _ => throw new InvalidDataException(
                $"Unsupported wait_for_artifact scope '{definition.Scope}'.")
        };

    private static string ResolveInside(
        string root,
        string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException(
                "Artifact condition paths must be relative.");

        var canonical = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(
            Path.Combine(canonical, relative));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!full.StartsWith(
                canonical + Path.DirectorySeparatorChar,
                comparison) &&
            !string.Equals(
                full,
                canonical,
                comparison))
            throw new InvalidDataException(
                "Artifact condition escapes its declared scope.");

        return full;
    }
}
