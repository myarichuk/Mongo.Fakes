using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class UpdateOperatorsE2ETests : IAsyncLifetime
{
    private MongoFakeServer? _server;
    private IMongoClient? _client;
    private IMongoCollection<BsonDocument>? _collection;

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
        _collection = _client.GetDatabase("testdb").GetCollection<BsonDocument>("updatecoll");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_server != null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task Pop_RemovesLastOrFirstElement()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "values", new BsonArray { 1, 2, 3 } } });

        await _collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 1),
            "{ $pop: { values: 1 } }");
        var afterLast = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 1)).FirstAsync();
        Assert.Equal(new[] { 1, 2 }, afterLast["values"].AsBsonArray.Select(v => v.AsInt32));

        await _collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 1),
            "{ $pop: { values: -1 } }");
        var afterFirst = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 1)).FirstAsync();
        Assert.Equal(new[] { 2 }, afterFirst["values"].AsBsonArray.Select(v => v.AsInt32));
    }

    [Fact]
    public async Task Min_OnlyReplacesWhenSmaller()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 2 }, { "score", 10 } });

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 2), "{ $min: { score: 15 } }");
        Assert.Equal(10, (await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 2)).FirstAsync())["score"].AsInt32);

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 2), "{ $min: { score: 3 } }");
        Assert.Equal(3, (await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 2)).FirstAsync())["score"].AsInt32);
    }

    [Fact]
    public async Task Max_OnlyReplacesWhenLarger()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 3 }, { "score", 10 } });

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 3), "{ $max: { score: 3 } }");
        Assert.Equal(10, (await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 3)).FirstAsync())["score"].AsInt32);

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 3), "{ $max: { score: 25 } }");
        Assert.Equal(25, (await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 3)).FirstAsync())["score"].AsInt32);
    }

    [Fact]
    public async Task PullAll_RemovesEveryMatchingValue()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 4 }, { "values", new BsonArray { 1, 2, 3, 2, 1 } } });

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 4), "{ $pullAll: { values: [1, 2] } }");

        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 4)).FirstAsync();
        Assert.Equal(new[] { 3 }, doc["values"].AsBsonArray.Select(v => v.AsInt32));
    }

    [Fact]
    public async Task CurrentDate_SetsBsonDateTime()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 5 } });

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 5), "{ $currentDate: { updatedAt: true } }");

        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 5)).FirstAsync();
        Assert.Equal(BsonType.DateTime, doc["updatedAt"].BsonType);
        Assert.True((DateTime.UtcNow - doc["updatedAt"].ToUniversalTime()) < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Push_WithEachSortSlice_AppliesInOrder()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 6 }, { "values", new BsonArray { 5 } } });

        await _collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 6),
            "{ $push: { values: { $each: [3, 8, 1], $sort: 1, $slice: 3 } } }");

        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 6)).FirstAsync();
        Assert.Equal(new[] { 1, 3, 5 }, doc["values"].AsBsonArray.Select(v => v.AsInt32));
    }

    [Fact]
    public async Task Push_ToMissingField_CreatesArrayWithValue()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 9 } });

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 9), "{ $push: { values: 1 } }");

        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 9)).FirstAsync();
        Assert.Equal(new[] { 1 }, doc["values"].AsBsonArray.Select(v => v.AsInt32));
    }

    [Fact]
    public async Task AddToSet_ToMissingField_CreatesArrayWithValue()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 10 } });

        await _collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 10), "{ $addToSet: { tags: 'a' } }");

        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 10)).FirstAsync();
        Assert.Equal(new[] { "a" }, doc["tags"].AsBsonArray.Select(v => v.AsString));
    }

    [Fact]
    public async Task AddToSet_WithEach_OnlyAddsDistinctValues()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 7 }, { "tags", new BsonArray { "a" } } });

        await _collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 7),
            "{ $addToSet: { tags: { $each: ['a', 'b', 'c'] } } }");

        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 7)).FirstAsync();
        Assert.Equal(new[] { "a", "b", "c" }, doc["tags"].AsBsonArray.Select(v => v.AsString));
    }

    [Fact]
    public async Task SetOnInsert_OnlyAppliesWhenUpserting()
    {
        await _collection!.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 8),
            "{ $set: { name: 'created' }, $setOnInsert: { createdBy: 'system' } }",
            new UpdateOptions { IsUpsert = true });

        var inserted = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 8)).FirstAsync();
        Assert.Equal("system", inserted["createdBy"].AsString);

        await _collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 8),
            "{ $set: { name: 'updated' }, $setOnInsert: { createdBy: 'should-not-apply' } }",
            new UpdateOptions { IsUpsert = true });

        var updated = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 8)).FirstAsync();
        Assert.Equal("system", updated["createdBy"].AsString);
        Assert.Equal("updated", updated["name"].AsString);
    }
}
