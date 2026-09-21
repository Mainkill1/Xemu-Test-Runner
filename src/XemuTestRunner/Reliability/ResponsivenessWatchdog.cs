namespace XemuTestRunner.Reliability;

public sealed record WatchdogTrip(int Failures, DateTimeOffset? LastSuccessUtc, string Error);
public sealed class ResponsivenessWatchdog
{
    private readonly WatchdogOptions _options;
    public ResponsivenessWatchdog(WatchdogOptions options) => _options = options;

    // The caller supplies QMP, not a guest heartbeat. A responsive control channel
    // is not proof that a game is making progress or rendering correctly.
    public async Task<WatchdogTrip> RunAsync(Func<CancellationToken, Task> probe, CancellationToken ct)
    {
        await Task.Delay(_options.StartupGraceMs, ct);
        int failures = 0;
        DateTimeOffset? lastSuccess = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_options.RequestTimeoutMs);
            try
            {
                await probe(deadline.Token);
                failures = 0;
                lastSuccess = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
            {
                if (++failures >= _options.FailureThreshold) return new(failures, lastSuccess, e.Message);
            }
            await Task.Delay(_options.IntervalMs, ct);
        }
    }
}
