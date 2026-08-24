using Mongo2Go;
using Mongo.Fakes.Core;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Mongo.Fakes.Server.Tests;

/// <summary>
/// The actual correctness backstop for filter semantics: run the same filter through a
/// real, ephemeral mongod and through the compiled Mongo.Fakes.Core predicate, and assert
/// identical result sets. See docs/SPEC.md#testing-strategy.
/// </summary>
public class FilterCompilerAgainstRealMongoTests : IDisposable
{
    private readonly MongoDbRunner? _runner;

    public FilterCompilerAgainstRealMongoTests()
    {
        _runner = OperatingSystem.IsLinux() ? MongoDbRunner.Start() : null;
    }

    [Fact]
    public void CompiledFilter_MatchesRealMongoResults()
    {
        if (_runner == null)
            throw new InvalidOperationException("Mongo2Go v5.x requires Linux with glibc 2.35+");

        var client = new MongoClient(_runner.ConnectionString);
        var collection = client.GetDatabase("smoketest").GetCollection<BsonDocument>("docs");

        collection.InsertMany(
        [
            BsonDocument.Parse("{ _id: 1, status: 'active', tags: ['admin'] }"),
            BsonDocument.Parse("{ _id: 2, status: 'inactive', tags: ['user'] }"),
            BsonDocument.Parse("{ _id: 3, status: 'active', tags: ['admin', 'user'] }"),
            BsonDocument.Parse("{ _id: 4, age: null }"),
            BsonDocument.Parse("{ _id: 5 }"),
        ]);

        var filter = BsonDocument.Parse("{ status: 'active', tags: 'admin' }");

        var mongoResults = collection.Find(filter).ToList()
            .Select(d => d["_id"].AsInt32)
            .OrderBy(id => id)
            .ToList();

        var allDocs = collection.Find(Builders<BsonDocument>.Filter.Empty).ToList();
        var predicate = new FilterCompiler().Compile(filter);
        var compiledResults = allDocs.Where(predicate)
            .Select(d => d["_id"].AsInt32)
            .OrderBy(id => id)
            .ToList();

        Assert.Equal(mongoResults, compiledResults);
    }

    public void Dispose() => _runner?.Dispose();
}
