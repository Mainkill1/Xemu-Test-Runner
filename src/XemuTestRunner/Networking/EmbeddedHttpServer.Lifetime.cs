namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var http = RunListenerLifetimeAsync(lifetime.Token);
        var requested = RunRequestedTestsAsync(lifetime.Token);
        try
        {
            // A failed component cannot silently leave the other one alive.
            var finished = await Task.WhenAny(http, requested).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(http, requested).ConfigureAwait(false);
        }
    }

    private async Task RunListenerLifetimeAsync(CancellationToken ct)
    {
        try { await RunHttpAsync(ct).ConfigureAwait(false); }
        // Windows may report disposal rather than cancellation when Stop races
        // a pending accept. Only an actually cancelled lifetime permits this.
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }
}
