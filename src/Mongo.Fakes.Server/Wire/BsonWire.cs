using System.Runtime.InteropServices;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Mongo.Fakes.Server.Wire;

/// <summary>
/// BSON reads and writes straight against the message buffers, without copying each
/// document into a scratch array first.
/// </summary>
internal static class BsonWire
{
    /// <summary>Wraps a request body in one read-only stream that each document is read from.</summary>
    public static MemoryStream OpenRead(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out var segment) && segment.Array != null)
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        return new MemoryStream(data.ToArray(), writable: false);
    }

    public static BsonDocument ReadDocument(MemoryStream stream, int offset)
    {
        stream.Position = offset;
        return BsonSerializer.Deserialize<BsonDocument>(stream);
    }

    public static void WriteDocument(MemoryStream output, BsonDocument document)
    {
        // Not disposed: disposing the writer would close the reused output stream.
        var writer = new BsonBinaryWriter(output);
        BsonSerializer.Serialize(writer, document);
        writer.Flush();
    }
}
