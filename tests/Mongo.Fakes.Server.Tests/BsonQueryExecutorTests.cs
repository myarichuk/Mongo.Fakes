using Mongo.Fakes.Server;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Tests;

public class BsonQueryExecutorTests
{
    [Fact]
    public void ExecuteFind_UsesSharedFilterCompiler_ForMatching()
    {
        var executor = new BsonQueryExecutor();
        var docs = new[]
        {
            BsonDocument.Parse("{ _id: 1, status: 'active', tags: ['admin'] }"),
            BsonDocument.Parse("{ _id: 2, status: 'inactive', tags: ['user'] }"),
            BsonDocument.Parse("{ _id: 3, status: 'active', tags: ['admin', 'user'] }"),
        };

        var filter = BsonDocument.Parse("{ status: 'active', tags: 'admin' }");
        var results = executor.ExecuteFind(docs, filter).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, d => Assert.Equal("active", d["status"].AsString));
    }
}
