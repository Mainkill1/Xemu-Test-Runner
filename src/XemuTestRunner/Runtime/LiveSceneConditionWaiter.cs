namespace XemuTestRunner.Runtime;

/// <summary>
/// Captures fresh images until the declared scene is visible, then publishes the
/// matching image into the durable result tree. Transient probes stay under the
/// control manager's .preview directory and are removed after inspection.
/// </summary>
public static class LiveSceneConditionWaiter
{
    public static async Task WaitAsync(
        ArtifactCheckDefinition condition,
        int timeoutMs,
        int pollIntervalMs,
        string resultDirectory,
        Func<CancellationToken, Task<string>> capture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(capture);
        if (!condition.Scope.Equals("result", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("wait_for_scene requires a result-scoped Condition.");
        if (timeoutMs <= 0)
            throw new InvalidDataException("wait_for_scene TimeoutMs must be greater than zero.");
        if (pollIntervalMs is < 25 or > 5000)
            throw new InvalidDataException("wait_for_scene PollIntervalMs must be between 25 and 5000.");

        var destination = RuntimeStateManager.ResolveInside(resultDirectory, condition.Path);
        var previewRoot = Path.GetFullPath(Path.Combine(resultDirectory, ".preview"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var lastDetail = "The first live scene capture did not finish.";

        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string? preview = null;
                try
                {
                    preview = await capture(deadline.Token).ConfigureAwait(false);
                    var inspection = await ArtifactInspector.CheckAsync(
                        condition, preview, deadline.Token).ConfigureAwait(false);
                    if (inspection.Passed)
                    {
                        await PublishAsync(preview, destination, deadline.Token).ConfigureAwait(false);
                        return;
                    }

                    lastDetail = inspection.Detail;
                }
                finally
                {
                    if (preview is not null && IsInside(previewRoot, preview))
                    {
                        try { File.Delete(preview); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }

                await Task.Delay(pollIntervalMs, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"wait_for_scene timed out after {timeoutMs} ms for " +
                $"'{condition.Scope}:{condition.Path}'. Last state: {lastDetail}");
        }
    }

    private static async Task PublishAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("wait_for_scene Condition.Path has no result directory.");
        Directory.CreateDirectory(directory);
        var staging = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsInside(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
