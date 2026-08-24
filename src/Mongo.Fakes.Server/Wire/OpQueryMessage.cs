using System.Buffers.Binary;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

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

        byte[] queryBytes = new byte[queryLength];
        span.Slice(offset, queryLength).CopyTo(queryBytes);
        using (var stream = new MemoryStream(queryBytes))
        {
            msg.Query = BsonSerializer.Deserialize<BsonDocument>(stream);
        }

        return msg;
    }

    public static byte[] BuildReply(BsonDocument body, int responseTo, int requestId)
    {
        var bodyBytes = body.ToBson();
        int messageLength = 36 + bodyBytes.Length;

        byte[] message = new byte[messageLength];
        var span = message.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), messageLength);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), responseTo);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12, 4), (int)OpCode.Reply);

        int responseFlags = 8;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), responseFlags);

        long cursorId = 0;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(20, 8), cursorId);

        int startingFrom = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), startingFrom);

        int numberReturned = 1;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(32, 4), numberReturned);

        Buffer.BlockCopy(bodyBytes, 0, message, 36, bodyBytes.Length);

        return message;
    }
}
