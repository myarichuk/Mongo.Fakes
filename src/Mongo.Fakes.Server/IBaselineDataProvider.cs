using MongoDB.Bson;

namespace Mongo.Fakes.Server;

public interface IBaselineDataProvider
{
    IReadOnlyList<BsonDocument> GetCollection(string database, string collection);
    IReadOnlyList<string> GetDatabases();
    IReadOnlyList<string> GetCollections(string database);
}
