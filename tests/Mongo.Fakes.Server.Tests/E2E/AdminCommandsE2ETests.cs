using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server.Tests.TestSupport;

namespace Mongo.Fakes.Server.Tests.E2E;

/// <summary>drop, create, renameCollection, collMod, dbStats, collStats, explain, serverStatus and database-level aggregate.</summary>
public class AdminCommandsE2ETests : IAsyncLifetime
{
    private FakeServer _fake = null!;
    private IMongoClient _client = null!;
    private IMongoDatabase _db = null!;
    private IMongoCollection<BsonDocument> _users = null!;

    public async Task InitializeAsync()
    {
        _fake = await FakeServer.StartAsync();
        _fake.Baseline
            .Add("testdb", "users", new BsonDocument { { "_id", 1 }, { "name", "ann" } }, new BsonDocument { { "_id", 2 }, { "name", "bob" } })
            .Add("testdb", "orders", new BsonDocument { { "_id", 1 }, { "total", 5 } });
        _client = _fake.CreateClient();
        _db = _client.GetDatabase("testdb");
        _users = _db.GetCollection<BsonDocument>("users");
    }

    public async Task DisposeAsync() => await _fake.DisposeAsync();

    private async Task<List<string>> CollectionNamesAsync(IMongoDatabase? db = null) =>
        (await (await (db ?? _db).ListCollectionNamesAsync()).ToListAsync()).OrderBy(n => n).ToList();

    [Fact]
    public async Task Drop_RemovesFixtureDocumentsAndTheCollection()
    {
        await _db.DropCollectionAsync("users");

        Assert.Equal(0, await _users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(["orders"], await CollectionNamesAsync());

        // The fixture _id is free again.
        await _users.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "name", "new" } });
        Assert.Equal(["orders", "users"], await CollectionNamesAsync());
    }

    [Fact]
    public async Task Drop_RemovesIndexes()
    {
        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("name"), new CreateIndexOptions { Unique = true }));
        await _db.DropCollectionAsync("users");

        await _users.InsertOneAsync(new BsonDocument("name", "ann"));
        await _users.InsertOneAsync(new BsonDocument("name", "ann"));
        Assert.Single((await _users.Indexes.ListAsync()).ToList());
    }

    [Fact]
    public async Task Drop_Missing_IsNamespaceNotFound()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RunCommandAsync<BsonDocument>(new BsonDocument("drop", "nope")));
        Assert.Equal(26, ex.Code);

        // The driver's DropCollection ignores it.
        await _db.DropCollectionAsync("nope");
    }

    [Fact]
    public async Task DropDatabase_RemovesEverything()
    {
        await _client.DropDatabaseAsync("testdb");

        Assert.Empty(await CollectionNamesAsync());
        Assert.DoesNotContain("testdb", await (await _client.ListDatabaseNamesAsync()).ToListAsync());
    }

    [Fact]
    public async Task Create_ThenExistsAndRejectsDuplicates()
    {
        await _db.CreateCollectionAsync("fresh");
        Assert.Contains("fresh", await CollectionNamesAsync());

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.CreateCollectionAsync("fresh"));
        Assert.Equal(48, ex.Code);
    }

    [Fact]
    public async Task ListDatabases_IncludesCreatedDatabases()
    {
        await _client.GetDatabase("other").GetCollection<BsonDocument>("c").InsertOneAsync(new BsonDocument());

        var names = await (await _client.ListDatabaseNamesAsync()).ToListAsync();
        Assert.Contains("other", names);
        Assert.Contains("testdb", names);

        var full = await (await _client.ListDatabasesAsync()).ToListAsync();
        Assert.True(full.Single(d => d["name"] == "testdb")["sizeOnDisk"].ToInt64() > 0);
    }

    [Fact]
    public async Task RenameCollection_MovesDocumentsAndIndexes()
    {
        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("name"), new CreateIndexOptions { Unique = true }));

        await _db.RenameCollectionAsync("users", "people");

        var people = _db.GetCollection<BsonDocument>("people");
        Assert.Equal(2, await people.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await _users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(["orders", "people"], await CollectionNamesAsync());

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => people.InsertOneAsync(new BsonDocument("name", "ann")));
        Assert.Contains("testdb.people", ex.Message);
    }

    [Fact]
    public async Task RenameCollection_OntoExisting_NeedsDropTarget()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RenameCollectionAsync("users", "orders"));
        Assert.Equal(48, ex.Code);

        await _db.RenameCollectionAsync("users", "orders", new RenameCollectionOptions { DropTarget = true });
        Assert.Equal(2, await _db.GetCollection<BsonDocument>("orders").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task RenameCollection_MissingSource_IsNamespaceNotFound()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RenameCollectionAsync("nope", "x"));
        Assert.Equal(26, ex.Code);
    }

    [Fact]
    public async Task CollMod_ChangesIndexExpiry()
    {
        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("createdAt"), new CreateIndexOptions { ExpireAfter = TimeSpan.FromSeconds(60) }));

        var reply = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "users" },
            { "index", new BsonDocument { { "keyPattern", new BsonDocument("createdAt", 1) }, { "expireAfterSeconds", 120 } } }
        });

        Assert.Equal(60, reply["expireAfterSeconds_old"].ToInt32());
        Assert.Equal(120, reply["expireAfterSeconds_new"].ToInt32());
        var index = (await _users.Indexes.ListAsync()).ToList().Single(i => i["name"] == "createdAt_1");
        Assert.Equal(120, index["expireAfterSeconds"].ToInt32());
    }

    [Fact]
    public async Task CollMod_MissingCollection_IsNamespaceNotFound()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RunCommandAsync<BsonDocument>(new BsonDocument("collMod", "nope")));
        Assert.Equal(26, ex.Code);
    }

    [Fact]
    public async Task DbStats_CountsCollectionsAndObjects()
    {
        var stats = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1));

        Assert.Equal("testdb", stats["db"].AsString);
        Assert.Equal(2, stats["collections"].ToInt32());
        Assert.Equal(3, stats["objects"].ToInt64());
        Assert.True(stats["dataSize"].ToDouble() > 0);
    }

    [Fact]
    public async Task CollStats_ReportsCountAndSize()
    {
        var stats = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("collStats", "users"));

        Assert.Equal("testdb.users", stats["ns"].AsString);
        Assert.Equal(2, stats["count"].ToInt32());
        Assert.True(stats["size"].ToDouble() > 0);
        Assert.Equal(1, stats["nindexes"].ToInt32());

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RunCommandAsync<BsonDocument>(new BsonDocument("collStats", "nope")));
        Assert.Equal(26, ex.Code);
    }

    [Fact]
    public async Task Explain_Find_ReportsPlanAndExecutionStats()
    {
        var reply = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", new BsonDocument { { "find", "users" }, { "filter", new BsonDocument("name", "ann") } } },
            { "verbosity", "executionStats" }
        });

        Assert.Equal("testdb.users", reply["queryPlanner"]["namespace"].AsString);
        Assert.Equal("COLLSCAN", reply["queryPlanner"]["winningPlan"]["stage"].AsString);
        Assert.Equal(1, reply["executionStats"]["nReturned"].ToInt64());
        Assert.Equal(2, reply["executionStats"]["totalDocsExamined"].ToInt32());
    }

    [Fact]
    public async Task Explain_QueryPlanner_DoesNotExecute()
    {
        var reply = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", new BsonDocument { { "delete", "users" }, { "deletes", new BsonArray { new BsonDocument { { "q", new BsonDocument() }, { "limit", 0 } } } } } },
            { "verbosity", "queryPlanner" }
        });

        Assert.False(reply.Contains("executionStats"));
        Assert.Equal(2, await _users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task ServerStatus_ReportsConnectionsAndOpCounters()
    {
        await _users.InsertManyAsync([new BsonDocument("_id", 10), new BsonDocument("_id", 11)]);
        await _users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        var status = await _client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("serverStatus", 1));

        Assert.Equal("mongod", status["process"].AsString);
        Assert.True(status["connections"]["current"].ToInt32() >= 1);
        Assert.True(status["opcounters"]["insert"].ToInt64() >= 2);
        Assert.True(status["opcounters"]["query"].ToInt64() >= 1);
        Assert.True(status["network"]["numRequests"].ToInt64() > 0);
        Assert.True(status["uptimeMillis"].ToInt64() >= 0);
    }

    [Fact]
    public async Task DatabaseAggregate_Documents()
    {
        var reply = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "aggregate", 1 },
            { "pipeline", new BsonArray
            {
                new BsonDocument("$documents", new BsonArray { new BsonDocument("x", 1), new BsonDocument("x", 2), new BsonDocument("x", 3) }),
                new BsonDocument("$match", new BsonDocument("x", new BsonDocument("$gte", 2)))
            }},
            { "cursor", new BsonDocument() }
        });

        Assert.Equal(new[] { 2, 3 }, reply["cursor"]["firstBatch"].AsBsonArray.Select(d => d["x"].AsInt32));
    }

    [Fact]
    public async Task DatabaseAggregate_OtherStage_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "aggregate", 1 },
            { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument()) } },
            { "cursor", new BsonDocument() }
        }));

        Assert.Equal(73, ex.Code);
    }

    [Fact]
    public async Task CurrentOp_ShowsARunningOperation()
    {
        var admin = _client.GetDatabase("admin");
        await admin.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", new BsonDocument("times", 1) },
            { "data", new BsonDocument { { "failCommands", new BsonArray { "find" } }, { "blockConnection", true }, { "blockTimeMS", 1000 } } }
        });

        var slowFind = _users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        await Task.Delay(200);

        var reply = await admin.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "aggregate", 1 },
            { "pipeline", new BsonArray
            {
                new BsonDocument("$currentOp", new BsonDocument()),
                new BsonDocument("$match", new BsonDocument("op", "query"))
            }},
            { "cursor", new BsonDocument() }
        });
        await slowFind;

        var op = reply["cursor"]["firstBatch"].AsBsonArray.Single().AsBsonDocument;
        Assert.Equal("testdb.users", op["ns"].AsString);
        Assert.True(op["active"].AsBoolean);
    }

    [Fact]
    public async Task CurrentOp_OutsideAdmin_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "aggregate", 1 },
            { "pipeline", new BsonArray { new BsonDocument("$currentOp", new BsonDocument()) } },
            { "cursor", new BsonDocument() }
        }));

        Assert.Equal(73, ex.Code);
    }
}
