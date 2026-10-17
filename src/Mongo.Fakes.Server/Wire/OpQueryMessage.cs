using System.Buffers.Binary;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Wire;

internal sealed class OpQueryMessage
{
    public int Flags { get; set; }
    public string FullCollectionName { get; set; } = string.Empty;
    public int NumberToSkip { get; set; }
    public int NumberToReturn { get; set; }
    public BsonDocument Query { get; set; } = new();

    public static OpQueryMessage Parse(ReadOnlyMemory<byte> data)
    {
        var msg = new OpQueryMessage();
        var span = data.Span;
        int offset = 0;

        if (offset + 4 > span.Length)
            throw new InvalidOperationException("Incomplete flags field.");
        msg.Flags = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        int nameEnd = offset;
        while (nameEnd < span.Length && span[nameEnd] != 0)
            nameEnd++;
        if (nameEnd >= span.Length)
            throw new InvalidOperationException("Unterminated collection name.");
        msg.FullCollectionName = System.Text.Encoding.UTF8.GetString(span.Slice(offset, nameEnd - offset));
        offset = nameEnd + 1;

        if (offset + 4 > span.Length)
            throw new InvalidOperationException("Incomplete numberToSkip.");
        msg.NumberToSkip = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (offset + 4 > span.Length)
            throw new InvalidOperationException("Incomplete numberToReturn.");
        msg.NumberToReturn = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        int queryLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        if (offset + queryLength > span.Length)
            throw new InvalidOperationException("Incomplete query document.");

        using (var stream = BsonWire.OpenRead(data))
        {
            msg.Query = BsonWire.ReadDocument(stream, offset);
        }

        return msg;
    }

    /// <summary>Writes an OP_REPLY into <paramref name="output"/>, replacing its contents.</summary>
    public static void WriteReply(MemoryStream output, BsonDocument body, int responseTo, int requestId)
    {
        const int prefixLength = 36;
        output.SetLength(0);

        Span<byte> prefix = stackalloc byte[prefixLength];
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(4, 4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(8, 4), responseTo);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(12, 4), (int)OpCode.Reply);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(16, 4), 8); // responseFlags: AwaitCapable
        BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(20, 8), 0); // cursorId
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(28, 4), 0); // startingFrom
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(32, 4), 1); // numberReturned
        output.Write(prefix);

        BsonWire.WriteDocument(output, body);
        BinaryPrimitives.WriteInt32LittleEndian(output.GetBuffer().AsSpan(0, 4), (int)output.Length);
    }
}
