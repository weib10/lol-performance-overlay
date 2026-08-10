using System.Text;
using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class BoundedStreamReaderTests
{
    [Fact]
    public async Task ChunkedPayloadAtLimitIsAccepted()
    {
        await using var source = new MemoryStream("synthetic"u8.ToArray());

        var result = await BoundedStreamReader.ReadUtf8Async(source, 9);

        Assert.Equal("synthetic", result);
    }

    [Fact]
    public async Task ChunkedPayloadOverLimitIsRejectedBeforeUnboundedGrowth()
    {
        await using var source = new MemoryStream(new byte[1_025]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedStreamReader.ReadBytesAsync(source, 1_024));
    }

    [Fact]
    public async Task InvalidUtf8IsRejected()
    {
        await using var source = new MemoryStream([0xff, 0xfe]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            BoundedStreamReader.ReadUtf8Async(source, 16));
    }

    [Fact]
    public async Task StalledBodyReadHonorsWholeResponseCancellation()
    {
        await using var source = new StalledReadStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedStreamReader.ReadBytesAsync(source, 1_024, cancellation.Token));
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
