using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     A response body that streams Server-Sent Events as the test sends them, so a test can act while a turn is
///     mid-stream. A read waits for the next event, the end, or a failure, and honours its cancellation token as the
///     browser's fetch stream does.
/// </summary>
internal sealed class SseFeed : Stream
{
    private readonly Channel<byte[]> _blocks = Channel.CreateUnbounded<byte[]>();
    private Exception? _failure;
    private byte[] _block = [];
    private int _position;

    /// <summary>Sends <paramref name="value"/> as one <c>data:</c> block, serialized as the API does.</summary>
    public SseFeed Send(object value)
    {
        _blocks.Writer.TryWrite(Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(value, JsonSerializerOptions.Web)}\n\n"));
        return this;
    }

    /// <summary>Ends the stream after the blocks sent so far.</summary>
    public SseFeed End()
    {
        _blocks.Writer.TryComplete();
        return this;
    }

    /// <summary>Fails the stream with <paramref name="failure"/> after the blocks sent so far, as a dropped connection does.</summary>
    public void Fail(Exception failure)
    {
        _failure = failure;
        _blocks.Writer.TryComplete();
    }

    /// <summary>A 200 response whose body is this feed.</summary>
    public HttpResponseMessage Response() => new(System.Net.HttpStatusCode.OK) { Content = new StreamContent(this) };

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position == _block.Length)
        {
            if (!await _blocks.Reader.WaitToReadAsync(cancellationToken))
            {
                return _failure is null ? 0 : throw _failure;
            }

            _block = await _blocks.Reader.ReadAsync(cancellationToken);
            _position = 0;
        }

        var count = Math.Min(buffer.Length, _block.Length - _position);
        _block.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
