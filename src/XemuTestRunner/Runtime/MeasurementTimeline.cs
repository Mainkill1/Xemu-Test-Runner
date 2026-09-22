using System.Diagnostics;
using System.Text.Json;

namespace XemuTestRunner.Runtime;

public sealed record MeasurementSegmentEvent(
    DateTimeOffset TimestampUtc,
    double ElapsedMs,
    string Action,
    string Name);

public sealed class MeasurementTimeline : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly long _started = Stopwatch.GetTimestamp();
    private string? _active;
    private bool _disposed;

    public MeasurementTimeline(string resultDirectory)
    {
        _writer = new StreamWriter(
            new FileStream(
                Path.Combine(resultDirectory, "segments.jsonl"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                16384),
            new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string? Active
    {
        get { lock (_gate) return _active; }
    }

    public void Start(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException(
                "Measurement segment name is required.");

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_active is not null)
                throw new InvalidOperationException(
                    $"Measurement segment '{_active}' is already active.");

            _active = name;
            Write("start", name);
        }
    }

    public void End(string name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_active is null)
                throw new InvalidOperationException(
                    "No measurement segment is active.");

            if (!string.Equals(
                    _active,
                    name,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Measurement segment '{_active}' is active, not '{name}'.");

            Write("end", name);
            _active = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_active is not null)
            {
                Write("abandoned", _active);
                _active = null;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(string action, string name)
    {
        var evt = new MeasurementSegmentEvent(
            DateTimeOffset.UtcNow,
            Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            action,
            name);

        _writer.WriteLine(JsonSerializer.Serialize(evt));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
