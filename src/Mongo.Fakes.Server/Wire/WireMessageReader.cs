using System.Buffers.Binary;
using System.Net.Sockets;

namespace Mongo.Fakes.Server.Wire;

internal sealed class WireMessageReader
{
    private const int HeaderSize = 16;
    private const int MaxMessageSize = 48 * 1024 * 1024;

    public static async Task<MessageHeader?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] headerBuffer = new byte[HeaderSize];
        try
        {
            await stream.ReadExactlyAsync(headerBuffer, 0, HeaderSize, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        int messageLength = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(0, 4));

        if (messageLength < HeaderSize || messageLength > MaxMessageSize)
            throw new IOException($"Invalid message length: {messageLength}");

        int requestId = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(4, 4));
        int responseTo = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(8, 4));
        OpCode opCode = (OpCode)BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(12, 4));

        return new MessageHeader(messageLength, requestId, responseTo, opCode);
    }

    public static async Task<byte[]> ReadBodyAsync(NetworkStream stream, int bodyLength, CancellationToken ct)
    {
        byte[] body = new byte[bodyLength];
        await stream.ReadExactlyAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        return body;
    }
}

internal static class NetworkStreamExtensions
{
    public static async Task ReadExactlyAsync(this NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct).ConfigureAwait(false);
            if (bytesRead == 0)
                throw new IOException("End of stream reached unexpectedly.");
            totalRead += bytesRead;
        }
    }
}
