using System.Diagnostics;
using System.Text.Json;

namespace XemuTestRunner.Reliability;

public sealed record ActivitySummary(
    bool Intervened,
    long ManualInputs,
    long Pauses,
    long Screenshots,
    long PreviewCaptures,
    long BulkTransfers,
    long Diagnostics);

public sealed class RunActivity : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _flushAt = Stopwatch.GetTimestamp();
    private long _inputs, _pauses, _screenshots, _previews, _transfers, _diagnostics;
    private bool _disposed;

    public RunActivity(string resultsDirectory)
    {
        _writer = new StreamWriter(
            new FileStream(
                Path.Combine(resultsDirectory, "operator-events.jsonl"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                16384),
            new System.Text.UTF8Encoding(false));
    }

    public void Mark(string kind, object? detail)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            switch (kind)
            {
                case "manual_input": _inputs++; break;
                case "pause": _pauses++; break;
                case "screenshot": _screenshots++; break;
                case "preview_capture": _previews++; break;
                case "bulk_transfer": _transfers++; break;
                case "diagnostic": _diagnostics++; break;
            }

            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                kind,
                detail
            }));

            if (Stopwatch.GetElapsedTime(_flushAt).TotalSeconds >= 1 ||
                kind is not "preview_capture")
            {
                _writer.Flush();
                _flushAt = Stopwatch.GetTimestamp();
            }
        }
    }

    public ActivitySummary Snapshot()
    {
        lock (_gate)
        {
            return new(
                _inputs + _pauses + _screenshots + _previews + _transfers + _diagnostics > 0,
                _inputs,
                _pauses,
                _screenshots,
                _previews,
                _transfers,
                _diagnostics);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}

public sealed class ActivityHub
{
    private readonly object _gate = new();
    private RunActivity? _current;
    private string? _runId;
    private int _transfers;

    public void Attach(string runId, RunActivity activity)
    {
        lock (_gate)
        {
            _runId = runId;
            _current = activity;
            if (_transfers > 0)
                activity.Mark("bulk_transfer", new { alreadyInFlight = _transfers });
        }
    }

    public IDisposable TrackTransfer(object? detail)
    {
        lock (_gate)
        {
            _current?.Mark("bulk_transfer", detail);
            _transfers++;
        }
        return new TransferLease(this);
    }

    public void Detach()
    {
        lock (_gate)
        {
            _runId = null;
            _current = null;
        }
    }

    public void Mark(string kind, object? detail)
    {
        lock (_gate)
            _current?.Mark(kind, detail);
    }

    public object Snapshot()
    {
        lock (_gate)
            return new { RunId = _runId, Activity = _current?.Snapshot() };
    }

    private sealed class TransferLease(ActivityHub owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                lock (owner._gate)
                    owner._transfers--;
            }
        }
    }
}
