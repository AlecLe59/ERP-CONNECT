using System.Buffers.Binary;
using ErpConnect.Rpc.Transport;

namespace ErpConnect.Rpc.Tests;

public class FramingTests
{
    [Fact]
    public async Task Round_trip()
    {
        var ms = new MemoryStream();
        await MessageFraming.WriteAsync(ms, "{\"a\":1}"u8.ToArray(), default);
        ms.Position = 0;
        (await MessageFraming.ReadAsync(ms, 1024, default)).ShouldBe("{\"a\":1}"u8.ToArray());
        (await MessageFraming.ReadAsync(ms, 1024, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Little_endian_header()
    {
        var ms = new MemoryStream();
        await MessageFraming.WriteAsync(ms, new byte[258], default);
        ms.ToArray()[..4].ShouldBe(new byte[] { 2, 1, 0, 0 });
    }

    // S12 : longueur annoncée trop grande, refusée sans lire (le flux ne contient même pas les données).
    [Fact]
    public async Task Oversized_message_is_refused_without_reading_content()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 5 * 1024 * 1024);
        var ms = new TrackingStream(header);
        var ex = await Should.ThrowAsync<FramingException>(() => MessageFraming.ReadAsync(ms, 4 * 1024 * 1024, default));
        ex.Message.ShouldContain("refusé");
        ms.BytesRead.ShouldBe(4);
    }

    [Fact]
    public async Task Announced_length_larger_than_data_is_refused()
    {
        var bytes = new byte[4 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 100);
        await Should.ThrowAsync<FramingException>(() => MessageFraming.ReadAsync(new MemoryStream(bytes), 1024, default));
    }

    [Fact]
    public async Task Truncated_header_and_empty_message_are_refused()
    {
        await Should.ThrowAsync<FramingException>(() => MessageFraming.ReadAsync(new MemoryStream([1, 0]), 1024, default));
        await Should.ThrowAsync<FramingException>(() => MessageFraming.ReadAsync(new MemoryStream([0, 0, 0, 0]), 1024, default));
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += n;
            return n;
        }
    }
}
