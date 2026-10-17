using System.Buffers.Binary;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Wire;

internal sealed class OpMsgMessage
{
    private const int FlagBitsSize = 4;
    private const int ChecksumSize = 4;

    public BsonDocument Body { get; private set; } = new();
    public bool MoreToCome { get; private set; }

    /// <summary>
    /// Reads only the moreToCome flag, so an error path can decide whether to reply
    /// without parsing the message again.
    /// </summary>
    public static bool ReadMoreToCome(ReadOnlySpan<byte> data) =>
        data.Length >= FlagBitsSize && (BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x02) != 0;

    public static OpMsgMessage Parse(ReadOnlyMemory<byte> data)
    {
        var msg = new OpMsgMessage();
        var span = data.Span;
        using var stream = BsonWire.OpenRead(data);
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

                msg.Body = BsonWire.ReadDocument(stream, offset - 4);

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

                    docs.Add(BsonWire.ReadDocument(stream, offset));

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

    /// <summary>Writes an OP_MSG reply into <paramref name="output"/>, replacing its contents.</summary>
    public static void WriteReply(MemoryStream output, BsonDocument body, int responseTo, int requestId)
    {
        const int prefixLength = 16 + FlagBitsSize + 1;
        output.SetLength(0);

        Span<byte> prefix = stackalloc byte[prefixLength];
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(4, 4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(8, 4), responseTo);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(12, 4), (int)OpCode.Msg);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.Slice(16, 4), 0); // flag bits
        prefix[20] = 0; // section kind 0: body
        output.Write(prefix);

        BsonWire.WriteDocument(output, body);
        BinaryPrimitives.WriteInt32LittleEndian(output.GetBuffer().AsSpan(0, 4), (int)output.Length);
    }
}
