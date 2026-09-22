namespace XemuTestRunner.Networking;

// Keep request read-ahead separate from response writes. A single BufferedStream
// cannot switch to writing while unread request-body bytes remain in its buffer,
// which prevented the server from returning early policy errors for uploads.
internal sealed class HttpDuplexStream : Stream
{
    private readonly Stream _transport;
    private readonly BufferedStream _reader;

    public HttpDuplexStream(Stream transport, int readBufferSize)
    {
        _transport = transport;
        _reader = new BufferedStream(transport, readBufferSize);
    }

    public override bool CanRead => _reader.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _transport.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _transport.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _transport.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) =>
        _reader.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        _transport.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _transport.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _reader.Dispose();
        base.Dispose(disposing);
    }
}
