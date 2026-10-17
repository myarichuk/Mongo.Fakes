using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server.Tests.TestSupport;

namespace Mongo.Fakes.Server.Tests.E2E;

public class ChangeStreamE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan MaxAwait = TimeSpan.FromMilliseconds(100);

    private FakeServer _fake = null!;
    private IMongoClient _client = null!;
    private IMongoDatabase _db = null!;
    private IMongoCollection<BsonDocument> _items = null!;

    public async Task InitializeAsync()
    {
        _fake = await FakeServer.StartAsync(new MongoFakeServerOptions { ReplicaSetName = "rs0" });
        _fake.Baseline.Add("testdb", "items", new BsonDocument { { "_id", 1 }, { "n", 1 } });
        _client = _fake.CreateClient();
        _db = _client.GetDatabase("testdb");
        _items = _db.GetCollection<BsonDocument>("items");
    }

    public async Task DisposeAsync() => await _fake.DisposeAsync();

    private static async Task<List<ChangeStreamDocument<BsonDocument>>> ReadAsync(IChangeStreamCursor<ChangeStreamDocument<BsonDocument>> cursor, int count)
    {
        var events = new List<ChangeStreamDocument<BsonDocument>>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (events.Count < count && DateTime.UtcNow < deadline && await cursor.MoveNextAsync())
            events.AddRange(cursor.Current);
        return events;
    }

    [Fact]
    public async Task Watch_SeesInsertUpdateReplaceDelete()
    {
        using var cursor = await _items.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait });

        await _items.InsertOneAsync(new BsonDocument { { "_id", 2 }, { "n", 2 } });
        await _items.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 2), Builders<BsonDocument>.Update.Set("n", 20).Unset("gone"));
        await _items.ReplaceOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 1), new BsonDocument("n", 100));
        await _items.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 2));

        var events = await ReadAsync(cursor, 4);

        Assert.Equal(
            [ChangeStreamOperationType.Insert, ChangeStreamOperationType.Update, ChangeStreamOperationType.Replace, ChangeStreamOperationType.Delete],
            events.Select(e => e.OperationType));
        Assert.Equal(2, events[0].FullDocument["n"].AsInt32);
        Assert.Equal(20, events[1].UpdateDescription.UpdatedFields["n"].AsInt32);
        Assert.Null(events[1].FullDocument);
        Assert.Equal(100, events[2].FullDocument["n"].AsInt32);
        Assert.Equal(new BsonDocument("_id", 2), events[3].DocumentKey);
        Assert.Equal("items", events[0].CollectionNamespace.CollectionName);
    }

    [Fact]
    public async Task Watch_UpdateLookup_IncludesFullDocument()
    {
        using var cursor = await _items.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait, FullDocument = ChangeStreamFullDocumentOption.UpdateLookup });

        await _items.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 1), Builders<BsonDocument>.Update.Inc("n", 1));

        var update = (await ReadAsync(cursor, 1)).Single();
        Assert.Equal(2, update.FullDocument["n"].AsInt32);
    }

    [Fact]
    public async Task Watch_PipelineFiltersEvents()
    {
        var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>()
            .Match(e => e.OperationType == ChangeStreamOperationType.Delete);
        using var cursor = await _items.WatchAsync(pipeline, new ChangeStreamOptions { MaxAwaitTime = MaxAwait });

        await _items.InsertOneAsync(new BsonDocument("_id", 5));
        await _items.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 5));

        var events = await ReadAsync(cursor, 1);
        Assert.Equal(ChangeStreamOperationType.Delete, events.Single().OperationType);
    }

    [Fact]
    public async Task Watch_OnlySeesItsOwnCollection()
    {
        using var cursor = await _items.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait });

        await _db.GetCollection<BsonDocument>("other").InsertOneAsync(new BsonDocument("_id", 1));
        await _items.InsertOneAsync(new BsonDocument("_id", 6));

        var events = await ReadAsync(cursor, 1);
        Assert.Equal(new BsonDocument("_id", 6), events.Single().DocumentKey);
    }

    [Fact]
    public async Task WatchDatabase_SeesEveryCollection()
    {
        using var cursor = await _db.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait });

        await _db.GetCollection<BsonDocument>("a").InsertOneAsync(new BsonDocument("_id", 1));
        await _db.GetCollection<BsonDocument>("b").InsertOneAsync(new BsonDocument("_id", 1));

        var events = await ReadAsync(cursor, 2);
        Assert.Equal(["a", "b"], events.Select(e => e.CollectionNamespace.CollectionName));
    }

    [Fact]
    public async Task Watch_ResumeAfterToken()
    {
        BsonDocument token;
        using (var cursor = await _items.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait }))
        {
            await _items.InsertOneAsync(new BsonDocument("_id", 7));
            token = (await ReadAsync(cursor, 1)).Single().ResumeToken;
        }

        await _items.InsertOneAsync(new BsonDocument("_id", 8));

        using var resumed = await _items.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait, ResumeAfter = token });
        var events = await ReadAsync(resumed, 1);
        Assert.Equal(new BsonDocument("_id", 8), events.Single().DocumentKey);
    }

    [Fact]
    public async Task Watch_DropInvalidatesTheStream()
    {
        using var cursor = await _items.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = MaxAwait });

        await _db.DropCollectionAsync("items");

        var events = await ReadAsync(cursor, 2);
        Assert.Equal([ChangeStreamOperationType.Drop, ChangeStreamOperationType.Invalidate], events.Select(e => e.OperationType));
    }

    [Fact]
    public async Task Watch_OnStandalone_IsRejected()
    {
        await using var standalone = await FakeServer.StartAsync();
        var items = standalone.CreateClient().GetDatabase("testdb").GetCollection<BsonDocument>("items");

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => items.WatchAsync());
        Assert.Equal(40573, ex.Code);
    }
}
