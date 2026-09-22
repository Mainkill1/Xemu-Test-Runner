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
        ArgumentNullException.ThrowIfNull(condition);
        if (timeoutMs <= 0)
        {
            throw new InvalidDataException("wait_for_artifact TimeoutMs must be greater than zero.");
        }

        if (pollIntervalMs is < 25 or > 5000)
        {
            throw new InvalidDataException("wait_for_artifact PollIntervalMs must be between 25 and 5000.");
        }

        var path = ArtifactInspector.ResolvePath(
            condition.Scope, condition.Path, packageDirectory, resultDirectory, runtime);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var lastDetail = "The first artifact read did not finish.";

        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var inspection = await ArtifactInspector.CheckAsync(
                    condition, path, deadline.Token).ConfigureAwait(false);
                if (inspection.Passed)
                {
                    return;
                }

                lastDetail = inspection.Detail;
                await Task.Delay(pollIntervalMs, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Covers expiration during file I/O as well as during the polling delay.
            throw new TimeoutException(
                $"wait_for_artifact timed out after {timeoutMs} ms for '{condition.Scope}:{condition.Path}'. Last state: {lastDetail}");
        }
    }
}
