using System.Buffers.Binary;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Mongo.Fakes.Server.Wire;

internal sealed class OpMsgMessage
{
    private const int FlagBitsSize = 4;
    private const int ChecksumSize = 4;

    public BsonDocument Body { get; private set; } = new();
    public bool MoreToCome { get; private set; }

    public static OpMsgMessage Parse(ReadOnlyMemory<byte> data)
    {
        var msg = new OpMsgMessage();
        var span = data.Span;
        int offset = 0;

        if (offset + FlagBitsSize > span.Length)
            throw new InvalidOperationException("Incomplete flag bits.");

        uint flagBits = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, FlagBitsSize));
        offset += FlagBitsSize;

        bool checksumPresent = (flagBits & 0x01) != 0;
        msg.MoreToCome = (flagBits & 0x02) != 0;

        if (checksumPresent)
        {
            if (span.Length < ChecksumSize)
                throw new InvalidOperationException("Message too short for checksum.");
            span = span.Slice(0, span.Length - ChecksumSize);
        }

        while (offset < span.Length)
        {
            if (offset + 1 > span.Length)
                break;

            byte kind = span[offset];
            offset++;

            if (kind == 0)
            {
                if (offset + 4 > span.Length)
                    throw new InvalidOperationException("Incomplete kind-0 section length.");

                int docLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
                offset += 4;

                if (offset - 4 + docLength > span.Length)
                    throw new InvalidOperationException("Incomplete kind-0 document.");

                byte[] docBytes = new byte[docLength];
                span.Slice(offset - 4, docLength).CopyTo(docBytes);
                using (var stream = new MemoryStream(docBytes))
                {
                    msg.Body = BsonSerializer.Deserialize<BsonDocument>(stream);
                }

                offset += docLength - 4;
            }
            else if (kind == 1)
            {
                if (offset + 4 > span.Length)
                    throw new InvalidOperationException("Incomplete kind-1 section size.");

                int sectionSize = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
                offset += 4;

                if (offset + sectionSize - 4 > span.Length)
                    throw new InvalidOperationException("Incomplete kind-1 section.");

                int sectionEnd = offset + sectionSize - 4;

                if (offset + 1 > sectionEnd)
                    throw new InvalidOperationException("Incomplete kind-1 identifier.");

                int identifierStart = offset;
                while (offset < sectionEnd && span[offset] != 0)
                    offset++;

                if (offset >= sectionEnd)
                    throw new InvalidOperationException("Unterminated kind-1 identifier.");

                string identifier = System.Text.Encoding.UTF8.GetString(span.Slice(identifierStart, offset - identifierStart));
                offset++;

                var docs = new List<BsonDocument>();
                while (offset < sectionEnd)
                {
                    if (offset + 4 > sectionEnd)
                        throw new InvalidOperationException("Incomplete document length in kind-1 section.");

                    int docLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));

                    if (offset + docLength > sectionEnd)
                        throw new InvalidOperationException("Incomplete document in kind-1 section.");

                    byte[] docBytes = new byte[docLength];
                    span.Slice(offset, docLength).CopyTo(docBytes);
                    using (var stream = new MemoryStream(docBytes))
                    {
                        docs.Add(BsonSerializer.Deserialize<BsonDocument>(stream));
                    }

                    offset += docLength;
                }

                msg.Body[identifier] = new BsonArray(docs);
            }
            else
            {
                throw new InvalidOperationException($"Unknown section kind: {kind}");
            }
        }

        return msg;
    }

    public static byte[] BuildReply(BsonDocument body, int responseTo, int requestId)
    {
        var bodyBytes = body.ToBson();
        int messageLength = 16 + 4 + 1 + bodyBytes.Length;

        byte[] message = new byte[messageLength];
        var span = message.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), messageLength);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), responseTo);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12, 4), (int)OpCode.Msg);

        uint flagBits = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), flagBits);

        byte kind0 = 0;
        span[20] = kind0;
        Buffer.BlockCopy(bodyBytes, 0, message, 21, bodyBytes.Length);

        return message;
    }
}
