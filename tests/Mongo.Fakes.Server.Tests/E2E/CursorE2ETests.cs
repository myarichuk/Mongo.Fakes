using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server.Tests.TestSupport;

namespace Mongo.Fakes.Server.Tests.E2E;

public class CursorE2ETests : IAsyncLifetime
{
    private FakeServer _fake = null!;
    private IMongoDatabase _db = null!;
    private IMongoCollection<BsonDocument> _items = null!;

    public async Task InitializeAsync()
    {
        _fake = await FakeServer.StartAsync(new MongoFakeServerOptions { CursorTimeout = TimeSpan.FromMilliseconds(300) });
        _fake.Baseline.Add("testdb", "items", Enumerable.Range(1, 10).Select(i => new BsonDocument { { "_id", i }, { "n", i } }).ToArray());
        _db = _fake.CreateClient().GetDatabase("testdb");
        _items = _db.GetCollection<BsonDocument>("items");
    }

    public async Task DisposeAsync() => await _fake.DisposeAsync();

    private Task<BsonDocument> RunAsync(BsonDocument command) => _db.RunCommandAsync<BsonDocument>(command);

    private static List<int> Ids(BsonValue batch) => batch.AsBsonArray.Select(d => d["_id"].AsInt32).ToList();

    [Fact]
    public async Task Find_HonoursBatchSize_AcrossGetMore()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 3 } });
        long id = first["cursor"]["id"].AsInt64;
        Assert.NotEqual(0, id);
        Assert.Equal([1, 2, 3], Ids(first["cursor"]["firstBatch"]));

        var seen = new List<int>(Ids(first["cursor"]["firstBatch"]));
        while (id != 0)
        {
            var more = await RunAsync(new BsonDocument { { "getMore", id }, { "collection", "items" }, { "batchSize", 3 } });
            var batch = Ids(more["cursor"]["nextBatch"]);
            Assert.True(batch.Count <= 3);
            seen.AddRange(batch);
            id = more["cursor"]["id"].AsInt64;
        }

        Assert.Equal(Enumerable.Range(1, 10), seen);
    }

    [Fact]
    public async Task Find_LastBatch_ReturnsCursorIdZero()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 6 } });
        var more = await RunAsync(new BsonDocument { { "getMore", first["cursor"]["id"].AsInt64 }, { "collection", "items" } });

        Assert.Equal(0, more["cursor"]["id"].AsInt64);
        Assert.Equal([7, 8, 9, 10], Ids(more["cursor"]["nextBatch"]));
        Assert.Equal("ok", more.GetElement(more.ElementCount - 1).Name);
    }

    [Fact]
    public async Task Find_LimitHoldsAcrossBatches()
    {
        var docs = await _items.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { BatchSize = 2 }).Limit(5).ToListAsync();

        Assert.Equal([1, 2, 3, 4, 5], docs.Select(d => d["_id"].AsInt32));
    }

    [Fact]
    public async Task Find_SmallResult_IsSingleBatch()
    {
        var reply = await RunAsync(new BsonDocument { { "find", "items" }, { "limit", 2 }, { "batchSize", 5 } });

        Assert.Equal(0, reply["cursor"]["id"].AsInt64);
        Assert.Equal(2, reply["cursor"]["firstBatch"].AsBsonArray.Count);
    }

    [Fact]
    public async Task Find_DefaultFirstBatch_Is101Documents()
    {
        await _items.InsertManyAsync(Enumerable.Range(100, 150).Select(i => new BsonDocument("_id", i)));

        var reply = await RunAsync(new BsonDocument("find", "items"));

        Assert.Equal(101, reply["cursor"]["firstBatch"].AsBsonArray.Count);
        Assert.NotEqual(0, reply["cursor"]["id"].AsInt64);
        Assert.Equal(160, (await _items.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).Count);
    }

    [Fact]
    public async Task Find_SingleBatch_ClosesCursor()
    {
        var reply = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 3 }, { "singleBatch", true } });

        Assert.Equal(0, reply["cursor"]["id"].AsInt64);
        Assert.Equal(3, reply["cursor"]["firstBatch"].AsBsonArray.Count);
    }

    [Fact]
    public async Task Aggregate_HonoursCursorBatchSize()
    {
        var first = await RunAsync(new BsonDocument
        {
            { "aggregate", "items" },
            { "pipeline", new BsonArray { new BsonDocument("$sort", new BsonDocument("n", -1)) } },
            { "cursor", new BsonDocument("batchSize", 4) }
        });
        Assert.Equal([10, 9, 8, 7], Ids(first["cursor"]["firstBatch"]));

        var docs = await (await _items.AggregateAsync<BsonDocument>(
            new BsonDocument[] { new("$match", new BsonDocument()) }, new AggregateOptions { BatchSize = 3 })).ToListAsync();
        Assert.Equal(10, docs.Count);
    }

    [Fact]
    public async Task GetMore_UnknownCursor_IsCursorNotFound()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() =>
            RunAsync(new BsonDocument { { "getMore", 123456789L }, { "collection", "items" } }));

        Assert.Equal(43, ex.Code);
    }

    [Fact]
    public async Task GetMore_OtherNamespace_IsRejected()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 1 } });

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() =>
            RunAsync(new BsonDocument { { "getMore", first["cursor"]["id"].AsInt64 }, { "collection", "other" } }));

        Assert.Equal(13, ex.Code);
    }

    [Fact]
    public async Task KillCursors_ReportsKilledAndNotFound()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 1 } });
        long id = first["cursor"]["id"].AsInt64;

        var reply = await RunAsync(new BsonDocument { { "killCursors", "items" }, { "cursors", new BsonArray { id, 42L } } });

        Assert.Equal(new BsonArray { id }, reply["cursorsKilled"]);
        Assert.Equal(new BsonArray { 42L }, reply["cursorsNotFound"]);
        Assert.Equal(new BsonArray(), reply["cursorsAlive"]);
        Assert.Equal(new BsonArray(), reply["cursorsUnknown"]);

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() =>
            RunAsync(new BsonDocument { { "getMore", id }, { "collection", "items" } }));
        Assert.Equal(43, ex.Code);
    }

    [Fact]
    public async Task IdleCursor_TimesOut()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 1 } });
        await Task.Delay(600);

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() =>
            RunAsync(new BsonDocument { { "getMore", first["cursor"]["id"].AsInt64 }, { "collection", "items" } }));

        Assert.Equal(43, ex.Code);
    }

    [Fact]
    public async Task NoCursorTimeout_IsExemptFromTimeout()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 1 }, { "noCursorTimeout", true } });
        await Task.Delay(600);

        var more = await RunAsync(new BsonDocument { { "getMore", first["cursor"]["id"].AsInt64 }, { "collection", "items" } });

        Assert.Equal(9, more["cursor"]["nextBatch"].AsBsonArray.Count);
    }

    [Fact]
    public async Task ActiveCursor_IsKeptAliveByGetMore()
    {
        var first = await RunAsync(new BsonDocument { { "find", "items" }, { "batchSize", 1 } });
        long id = first["cursor"]["id"].AsInt64;

        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(150);
            var more = await RunAsync(new BsonDocument { { "getMore", id }, { "collection", "items" }, { "batchSize", 1 } });
            Assert.Single(more["cursor"]["nextBatch"].AsBsonArray);
        }
    }

    [Fact]
    public async Task ClosingConnection_FreesItsCursors()
    {
        // One pooled connection, so the cursor and the ping below share it. (Disposing a MongoClient
        // doesn't close its sockets, so the server drops the connection via a failpoint instead.)
        var other = _fake.CreateClient(s =>
        {
            s.ApplicationName = "short-lived";
            s.MaxConnectionPoolSize = 1;
            s.RetryReads = false;
        }).GetDatabase("testdb");
        await other.RunCommandAsync<BsonDocument>(new BsonDocument { { "find", "items" }, { "batchSize", 1 }, { "noCursorTimeout", true } });
        Assert.Equal(1, _fake.Server.OpenCursorCount);

        await _db.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", new BsonDocument("times", 1) },
            { "data", new BsonDocument { { "failCommands", new BsonArray { "ping" } }, { "closeConnection", true }, { "appName", "short-lived" } } }
        });
        await Assert.ThrowsAnyAsync<MongoException>(() => other.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_fake.Server.OpenCursorCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(0, _fake.Server.OpenCursorCount);
    }
}
