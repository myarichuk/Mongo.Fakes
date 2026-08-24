using MongoDB.Bson;

namespace Mongo.Fakes.Server;

public interface IMongoBackend
{
    Task<BsonDocument> ExecuteCommandAsync(string database, BsonDocument command, CancellationToken ct);
}
