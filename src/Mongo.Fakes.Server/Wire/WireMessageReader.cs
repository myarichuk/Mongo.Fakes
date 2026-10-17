using System.Buffers.Binary;
using System.Net.Sockets;

namespace Mongo.Fakes.Server.Wire;

internal sealed class WireMessageReader
{
    public const int HeaderSize = 16;
    private const int MaxMessageSize = 48 * 1024 * 1024;

    /// <param name="headerBuffer">A per-connection buffer of at least <see cref="HeaderSize"/> bytes.</param>
    public static async Task<MessageHeader?> ReadHeaderAsync(NetworkStream stream, byte[] headerBuffer, CancellationToken ct)
    {
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

    /// <summary>Reads the body into <paramref name="buffer"/>, which may be larger than the body (a pooled array).</summary>
    public static async Task ReadBodyAsync(NetworkStream stream, byte[] buffer, int bodyLength, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(buffer, 0, bodyLength, ct).ConfigureAwait(false);
    }
}
