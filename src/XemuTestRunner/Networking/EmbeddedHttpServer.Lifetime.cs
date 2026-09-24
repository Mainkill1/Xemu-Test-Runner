using System.Diagnostics;

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
            try { await Task.WhenAll(http, requested).ConfigureAwait(false); }
            finally { await DrainAgentRequestsAsync().ConfigureAwait(false); }
        }
    }

    private async Task RunListenerLifetimeAsync(CancellationToken ct)
    {
        try { await RunHttpAsync(ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }

    private async Task DrainAgentRequestsAsync()
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            bool pending;
            lock (_agentStoreGate) pending = _agentStore?.HasPendingOperations ?? false;
            // Handlers can still publish a final receipt after their connection
            // is closed. Do not release the server lifetime while those writers
            // still own the workspace. Waiting is bounded and never reruns work.
            if (_clients.IsEmpty && !pending) return;
            if (timer.Elapsed >= TimeSpan.FromSeconds(5))
                throw new TimeoutException("HTTP handlers or agent operations did not finish shutdown; workspace ownership must not be assumed released.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
