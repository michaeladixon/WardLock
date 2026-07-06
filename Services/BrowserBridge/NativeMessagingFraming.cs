using System.IO;
using System.Text;
using System.Text.Json;

namespace WardLock.Services.BrowserBridge;

/// <summary>
/// Chrome native messaging wire format: a 4-byte little-endian length prefix
/// followed by a UTF-8 JSON document. The same framing is reused on the named
/// pipe between the browser-launched proxy process and the running app.
/// </summary>
public static class NativeMessagingFraming
{
    /// <summary>Sanity cap — native messaging responses are limited to 1 MB anyway.</summary>
    private const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Reads one framed JSON message, or null on clean end-of-stream.</summary>
    public static JsonDocument? ReadMessage(Stream stream)
    {
        var lengthBytes = ReadExactly(stream, 4);
        if (lengthBytes == null) return null;

        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidDataException($"Invalid message length: {length}");

        var payload = ReadExactly(stream, length)
            ?? throw new InvalidDataException("Stream ended mid-message.");
        return JsonDocument.Parse(payload);
    }

    public static void WriteMessage(Stream stream, object message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        WriteRaw(stream, json);
    }

    /// <summary>Writes pre-serialized JSON bytes with the length prefix.</summary>
    public static void WriteRaw(Stream stream, byte[] utf8Json)
    {
        stream.Write(BitConverter.GetBytes(utf8Json.Length));
        stream.Write(utf8Json);
        stream.Flush();
    }

    public static byte[] ToUtf8(JsonDocument doc)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            doc.WriteTo(writer);
        return ms.ToArray();
    }

    private static byte[]? ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
                return offset == 0 ? null : throw new InvalidDataException("Stream ended mid-frame.");
            offset += read;
        }
        return buffer;
    }
}
