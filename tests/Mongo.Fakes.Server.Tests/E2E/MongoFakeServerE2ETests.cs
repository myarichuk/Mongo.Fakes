using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class MongoFakeServerE2ETests : IAsyncLifetime
{
    private MongoFakeServer? _server;
    private IMongoClient? _client;

    public async Task InitializeAsync()
    {
        var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
        _server = new MongoFakeServer(backend, port: 0);
        await _server.StartAsync();

        var settings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _server.Port)
        };
        _client = new MongoClient(settings);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_server != null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task Ping_Should_Succeed()
    {
        var db = _client!.GetDatabase("admin");
        var result = await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument { { "ping", 1 } });
        Assert.NotNull(result);
        Assert.Equal(1.0, result["ok"].AsDouble);
    }

    [Fact]
    public async Task Insert_Should_Succeed()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll");
        var doc = new MongoDB.Bson.BsonDocument { { "name", "Alice" }, { "age", 30 } };

        await collection.InsertOneAsync(doc);

        var found = await collection.Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Empty).FirstOrDefaultAsync();
        Assert.NotNull(found);
        Assert.Equal("Alice", found["name"].AsString);
        Assert.Equal(30, found["age"].AsInt32);
    }

    [Fact]
    public async Task Insert_With_DuplicateId_Should_Return_WriteError()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll2");
        var doc1 = new MongoDB.Bson.BsonDocument { { "_id", "doc1" }, { "value", 1 } };
        var doc2 = new MongoDB.Bson.BsonDocument { { "_id", "doc1" }, { "value", 2 } };

        await collection.InsertOneAsync(doc1);

        var ex = await Assert.ThrowsAsync<MongoDB.Driver.MongoWriteException>(() => collection.InsertOneAsync(doc2));
        Assert.Equal(11000, ex.WriteError.Code);
    }

    [Fact]
    public async Task Update_Should_Replace_Document()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll3");
        var doc = new MongoDB.Bson.BsonDocument { { "_id", "doc1" }, { "name", "Alice" }, { "age", 30 } };

        await collection.InsertOneAsync(doc);

        var replacement = new MongoDB.Bson.BsonDocument { { "name", "Bob" }, { "age", 40 } };
        var result = await collection.ReplaceOneAsync(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "doc1"),
            replacement);

        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(1, result.ModifiedCount);

        var found = await collection.Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "doc1")).FirstOrDefaultAsync();
        Assert.NotNull(found);
        Assert.Equal("Bob", found["name"].AsString);
        Assert.Equal(40, found["age"].AsInt32);
    }

    [Fact]
    public async Task Delete_Should_Remove_Documents()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll4");
        var doc1 = new MongoDB.Bson.BsonDocument { { "_id", "doc1" }, { "value", 1 } };
        var doc2 = new MongoDB.Bson.BsonDocument { { "_id", "doc2" }, { "value", 2 } };

        await collection.InsertManyAsync(new[] { doc1, doc2 });

        var result = await collection.DeleteOneAsync(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "doc1"));
        Assert.Equal(1, result.DeletedCount);

        var found = await collection.Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Empty).ToListAsync();
        Assert.Single(found);
        Assert.Equal("doc2", found[0]["_id"].AsString);
    }
}
