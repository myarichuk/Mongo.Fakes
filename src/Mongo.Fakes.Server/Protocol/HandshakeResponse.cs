using MongoDB.Bson;

namespace Mongo.Fakes.Server.Protocol;

internal static class HandshakeResponse
{
    private static int _connectionIdCounter;

    public static BsonDocument CreateHello()
    {
        int connectionId = Interlocked.Increment(ref _connectionIdCounter);

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "isWritablePrimary", true },
            { "ismaster", true },
            { "helloOk", true },
            { "maxWireVersion", 17 },
            { "minWireVersion", 0 },
            { "maxBsonObjectSize", 16777216 },
            { "maxMessageSizeBytes", 48000000 },
            { "maxWriteBatchSize", 100000 },
            { "localTime", DateTime.UtcNow },
            { "logicalSessionTimeoutMinutes", 30 },
            { "connectionId", connectionId },
            { "readOnly", false }
        };
    }
}
