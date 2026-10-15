using System.IO;
using System.Text;
using WardLock.Services.BrowserBridge;

namespace WardLock.Tests;

public class NativeMessagingFramingTests
{
    [Fact]
    public void Round_trips_a_message_with_length_prefix()
    {
        using var ms = new MemoryStream();
        NativeMessagingFraming.WriteMessage(ms, new { type = "hello", n = 42 });

        var bytes = ms.ToArray();
        Assert.Equal(bytes.Length - 4, BitConverter.ToInt32(bytes, 0));

        ms.Position = 0;
        using var doc = NativeMessagingFraming.ReadMessage(ms);
        Assert.NotNull(doc);
        Assert.Equal("hello", doc!.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public void Returns_null_on_clean_end_of_stream()
    {
        using var ms = new MemoryStream();
        Assert.Null(NativeMessagingFraming.ReadMessage(ms));
    }

    [Fact]
    public void Reassembles_frames_delivered_in_one_byte_reads()
    {
        using var buffer = new MemoryStream();
        NativeMessagingFraming.WriteMessage(buffer, new { type = "list" });
        using var trickle = new OneByteStream(buffer.ToArray());

        using var doc = NativeMessagingFraming.ReadMessage(trickle);
        Assert.Equal("list", doc!.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1024 * 1024 + 1)]
    public void Rejects_invalid_lengths(int length)
    {
        using var ms = new MemoryStream(BitConverter.GetBytes(length));
        Assert.Throws<InvalidDataException>(() => NativeMessagingFraming.ReadMessage(ms));
    }

    [Fact]
    public void Rejects_truncated_payload()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"x\"}");
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(payload.Length));
        ms.Write(payload, 0, payload.Length - 3);
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => NativeMessagingFraming.ReadMessage(ms));
    }

    [Fact]
    public void Rejects_truncated_length_prefix()
    {
        using var ms = new MemoryStream([1, 0]);
        Assert.Throws<InvalidDataException>(() => NativeMessagingFraming.ReadMessage(ms));
    }

    /// <summary>Simulates a pipe that returns at most one byte per Read call.</summary>
    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, 1));
    }
}
