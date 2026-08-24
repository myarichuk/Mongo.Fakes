using Mongo2Go;
using Mongo.Fakes.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

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
    [Trait("Category", "RequiresMongod")]
    public void CompiledFilter_MatchesRealMongoResults()
    {
        if (_runner == null)
            throw new InvalidOperationException(
                "Mongo2Go ships Windows binaries and starts mongod fine in a plain console app on this " +
                "machine, but mongod never comes up when launched from a VSTest testhost process on Windows " +
                "(connection refused, root cause not identified). This test only runs reliably on Linux for now.");

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
