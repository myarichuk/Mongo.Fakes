using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class IndexOperationsE2ETests : IAsyncLifetime
{
    private MongoFakeServer _server = null!;
    private IMongoClient _client = null!;
    private IMongoCollection<BsonDocument> _coll = null!;

    public async Task InitializeAsync()
    {
        var backend = new BsonFileBackend(
            Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
        _server = new MongoFakeServer(backend, port: 0);
        await _server.StartAsync();

        var settings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _server.Port)
        };
        _client = new MongoClient(settings);
        _coll = _client.GetDatabase("testdb").GetCollection<BsonDocument>("testcoll");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task CreateIndex_SingleField_IsStored()
    {
        // Create a single-field index
        var indexName = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email")
            )
        );

        Assert.NotEmpty(indexName);
        Assert.Equal("email_1", indexName);
    }

    [Fact]
    public async Task CreateIndex_Compound_IsStored()
    {
        // Create compound index
        var indexName = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("userId")
                    .Ascending("createdAt")
            )
        );

        Assert.NotEmpty(indexName);
    }

    [Fact]
    public async Task ListIndexes_IncludesAllCreated()
    {
        // Create indexes
        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("field1")
            )
        );

        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("field2")
            )
        );

        // List should include _id + 2 created = 3 total
        var indexDocs = (await _coll.Indexes.ListAsync()).ToList();
        Assert.Equal(3, indexDocs.Count);
    }

    [Fact]
    public async Task UniqueIndex_PreventsDuplicates()
    {
        // Create unique index
        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("code"),
                new CreateIndexOptions { Unique = true }
            )
        );

        // Insert first document
        await _coll.InsertOneAsync(new BsonDocument { { "code", "ABC" } });

        // Try to insert duplicate
        var ex = await Assert.ThrowsAsync<MongoWriteException>(async () =>
        {
            await _coll.InsertOneAsync(new BsonDocument { { "code", "ABC" } });
        });

        Assert.Contains("E11000", ex.Message);
    }

    [Fact]
    public async Task DropIndex_ByName_RemovesIndex()
    {
        // Create index
        var indexName = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tempField")
            )
        );

        // Drop it
        await _coll.Indexes.DropOneAsync(indexName);

        // Verify it's gone
        var indexes = (await _coll.Indexes.ListAsync()).ToList();
        Assert.DoesNotContain(indexes, i => i["name"].AsString == indexName);
    }

    [Fact]
    public async Task DropAll_RemovesNonIdIndexes()
    {
        // Create multiple indexes
        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("f1")
            )
        );

        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("f2")
            )
        );

        // Drop all
        await _coll.Indexes.DropAllAsync();

        // Only _id should remain
        var indexes = (await _coll.Indexes.ListAsync()).ToList();
        Assert.Single(indexes);
        Assert.Equal("_id_", indexes[0]["name"].AsString);
    }

    [Fact]
    public async Task CreateIndex_Idempotent_OnDuplicate()
    {
        var idx1 = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("field1")
            )
        );

        // Create identical index again
        var idx2 = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("field1")
            )
        );

        // Should succeed and return same name
        Assert.Equal(idx1, idx2);
    }

    [Fact]
    public async Task SparseIndex_IsTracked()
    {
        // Create sparse index
        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("optional"),
                new CreateIndexOptions { Sparse = true }
            )
        );

        var indexes = (await _coll.Indexes.ListAsync()).ToList();
        var sparseIdx = indexes.FirstOrDefault(i =>
            i["name"].AsString == "optional_1"
        );

        Assert.NotNull(sparseIdx);
        Assert.True(sparseIdx["sparse"].ToBoolean());
    }

    [Fact]
    public async Task UniqueIndex_IsTracked()
    {
        // Create unique index
        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("username"),
                new CreateIndexOptions { Unique = true }
            )
        );

        var indexes = (await _coll.Indexes.ListAsync()).ToList();
        var uniqueIdx = indexes.FirstOrDefault(i =>
            i["name"].AsString == "username_1"
        );

        Assert.NotNull(uniqueIdx);
        Assert.True(uniqueIdx["unique"].ToBoolean());
    }

    [Fact]
    public async Task CannotDropIdIndex()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(async () =>
        {
            await _coll.Indexes.DropOneAsync("_id_");
        });

        Assert.Contains("cannot drop _id index", ex.Message);
    }

    [Fact]
    public async Task CustomIndexName_IsPreserved()
    {
        // Create index with custom name
        var indexName = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("field1"),
                new CreateIndexOptions { Name = "my_custom_idx" }
            )
        );

        Assert.Equal("my_custom_idx", indexName);

        // Verify in list
        var indexes = (await _coll.Indexes.ListAsync()).ToList();
        Assert.Contains(indexes, i => i["name"].AsString == "my_custom_idx");
    }

    [Fact]
    public async Task DescendingIndex_IsCreated()
    {
        var indexName = await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Descending("timestamp")
            )
        );

        Assert.NotEmpty(indexName);
        Assert.StartsWith("timestamp_", indexName);
    }

    [Fact]
    public async Task CompoundUniqueIndex_PreventsDuplicates()
    {
        // Create unique compound index
        await _coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("userId")
                    .Ascending("email"),
                new CreateIndexOptions { Unique = true }
            )
        );

        // Insert first document
        await _coll.InsertOneAsync(new BsonDocument
        {
            { "userId", 1 },
            { "email", "test@example.com" }
        });

        // Duplicate should fail
        var ex = await Assert.ThrowsAsync<MongoWriteException>(async () =>
        {
            await _coll.InsertOneAsync(new BsonDocument
            {
                { "userId", 1 },
                { "email", "test@example.com" }
            });
        });

        Assert.Contains("E11000", ex.Message);

        // But different userId should work
        await _coll.InsertOneAsync(new BsonDocument
        {
            { "userId", 2 },
            { "email", "test@example.com" }
        });

        var docs = await _coll.Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, docs.Count);
    }
}
