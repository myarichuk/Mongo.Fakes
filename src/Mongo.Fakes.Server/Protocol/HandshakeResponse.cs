using MongoDB.Bson;

namespace Mongo.Fakes.Server.Protocol;

internal static class HandshakeResponse
{
    public const string Version = "6.0.0";

    private static readonly ObjectId ElectionId = ObjectId.GenerateNewId();

    public static BsonDocument CreateHello(int connectionId, MongoFakeServerOptions options, int port, ObjectId processId)
    {
        var hello = new BsonDocument
        {
            { "helloOk", true },
            { "isWritablePrimary", true },
            { "ismaster", true },
            { "topologyVersion", new BsonDocument { { "processId", processId }, { "counter", 0L } } }
        };

        if (options.ReplicaSetName != null)
        {
            string me = $"{options.AdvertisedHost}:{port}";
            var now = DateTime.UtcNow;
            var opTime = new BsonDocument
            {
                { "ts", new BsonTimestamp((int)new DateTimeOffset(now).ToUnixTimeSeconds(), 1) },
                { "t", 1L }
            };

            hello["hosts"] = new BsonArray { me };
            hello["setName"] = options.ReplicaSetName;
            hello["setVersion"] = 1;
            hello["secondary"] = false;
            hello["primary"] = me;
            hello["me"] = me;
            hello["electionId"] = ElectionId;
            hello["lastWrite"] = new BsonDocument
            {
                { "opTime", opTime },
                { "lastWriteDate", now },
                { "majorityOpTime", opTime },
                { "majorityWriteDate", now }
            };
        }

        hello["maxBsonObjectSize"] = 16777216;
        hello["maxMessageSizeBytes"] = 48000000;
        hello["maxWriteBatchSize"] = 100000;
        hello["localTime"] = DateTime.UtcNow;
        hello["logicalSessionTimeoutMinutes"] = 30;
        hello["connectionId"] = connectionId;
        hello["minWireVersion"] = 0;
        hello["maxWireVersion"] = 17;
        hello["readOnly"] = false;
        hello["ok"] = 1.0;
        return hello;
    }
}
