namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// Wraps a byte source so <see cref="System.Net.Http.StreamContent"/> can never discover its length
/// (<see cref="CanSeek"/> is false, <see cref="Length"/> throws) - this is what makes
/// <see cref="System.Net.Http.HttpClient"/> send the request chunked, no <c>Content-Length</c> header, the
/// same shape real ffmpeg's <c>-method PUT -http_persistent 1</c> segment upload takes (PROTOCOL.md
/// "Ingest").
/// </summary>
internal sealed class NonSeekableStream : Stream
{
    private readonly Stream _inner;

    public NonSeekableStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>Trickles <paramref name="data"/> out in chunks with a delay between each - for tests asserting the target file is never visible partially written.</summary>
    public static NonSeekableStream Trickle(byte[] data, int chunkSize, TimeSpan delayBetweenChunks) =>
        new(new TrickleStream(data, chunkSize, delayBetweenChunks));

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("anemone-test: length is deliberately unknown, forcing chunked transfer");

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;
        private int _position;

        public TrickleStream(byte[] data, int chunkSize, TimeSpan delay)
        {
            _data = data;
            _chunkSize = chunkSize;
            _delay = delay;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            if (_position > 0)
            {
                // Not before the very first chunk: lets the caller observe the pre-upload state first.
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }

            var toCopy = Math.Min(Math.Min(_chunkSize, count), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
