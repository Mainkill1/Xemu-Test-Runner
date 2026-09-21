using System.Diagnostics;

namespace XemuTestRunner.Reliability;

public sealed record PreviewFrame(byte[] Bytes, string RunId, DateTimeOffset CapturedUtc);
public sealed class PreviewCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _run;
    private long _attemptAt;
    private PreviewFrame? _frame;
    private string? _error;

    public async Task<PreviewFrame> GetAsync(string runId, int intervalMs,
        Func<CancellationToken, Task<byte[]>> capture, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_run == runId && Stopwatch.GetElapsedTime(_attemptAt).TotalMilliseconds < intervalMs)
            {
                if (_error is not null) throw new IOException(_error);
                if (_frame is not null) return _frame;
            }
            _run = runId; _frame = null; _error = null;
            try
            {
                var bytes = await capture(ct);
                _frame = new(bytes, runId, DateTimeOffset.UtcNow);
                return _frame;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { _error = e.Message; throw; }
            finally { _attemptAt = Stopwatch.GetTimestamp(); }
        }
        finally { _gate.Release(); }
    }
}
