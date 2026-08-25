using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class ArrayElemAtAndFindAndModifyE2ETests : IAsyncLifetime
{
    private MongoFakeServer? _server;
    private IMongoClient? _client;
    private IMongoCollection<BsonDocument>? _collection;
    private IMongoDatabase? _database;

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
        _database = _client.GetDatabase("testdb");
        _collection = _database.GetCollection<BsonDocument>("test");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_server != null)
            await _server.DisposeAsync();
    }

    // $arrayElemAt tests
    [Fact]
    public async Task ArrayElemAt_WithPositiveIndex()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "items", new BsonArray { "a", "b", "c" } } });

        var result = await _collection
            .Aggregate<BsonDocument>()
            .AppendStage<BsonDocument>("{ $project: { first: { $arrayElemAt: ['$items', 0] }, second: { $arrayElemAt: ['$items', 1] } } }")
            .FirstAsync();

        Assert.Equal("a", result["first"].AsString);
        Assert.Equal("b", result["second"].AsString);
    }

    [Fact]
    public async Task ArrayElemAt_WithNegativeIndex()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 2 }, { "items", new BsonArray { "a", "b", "c" } } });

        var result = await _collection
            .Aggregate<BsonDocument>()
            .AppendStage<BsonDocument>("{ $project: { last: { $arrayElemAt: ['$items', -1] }, secondLast: { $arrayElemAt: ['$items', -2] } } }")
            .FirstAsync();

        Assert.Equal("c", result["last"].AsString);
        Assert.Equal("b", result["secondLast"].AsString);
    }

    [Fact]
    public async Task ArrayElemAt_WithOutOfRangeIndex()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 3 }, { "items", new BsonArray { "a", "b" } } });

        var result = await _collection
            .Aggregate<BsonDocument>()
            .AppendStage<BsonDocument>("{ $project: { elem: { $arrayElemAt: ['$items', 10] } } }")
            .FirstAsync();

        Assert.Equal(BsonType.Null, result["elem"].BsonType);
    }

    [Fact]
    public async Task ArrayElemAt_WithNullArray()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 4 } });

        var result = await _collection
            .Aggregate<BsonDocument>()
            .AppendStage<BsonDocument>("{ $project: { elem: { $arrayElemAt: ['$items', 0] } } }")
            .FirstAsync();

        Assert.Equal(BsonType.Null, result["elem"].BsonType);
    }

    // findAndModify tests with operator updates
    [Fact]
    public async Task FindAndModify_WithOperatorUpdate_ReturnsPreImage()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 10 }, { "count", 5 }, { "name", "test" } });

        var cmd = new BsonDocument
        {
            { "findandmodify", "test" },
            { "query", new BsonDocument { { "_id", 10 } } },
            { "update", new BsonDocument { { "$inc", new BsonDocument { { "count", 3 } } } } }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        var resultDoc = result["value"].AsBsonDocument;

        Assert.Equal(5, resultDoc["count"].AsInt32);

        var updated = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 10)).FirstAsync();
        Assert.Equal(8, updated["count"].AsInt32);
    }

    [Fact]
    public async Task FindAndModify_WithOperatorUpdate_ReturnsPostImage()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 11 }, { "count", 5 }, { "name", "test" } });

        var cmd = new BsonDocument
        {
            { "findandmodify", "test" },
            { "query", new BsonDocument { { "_id", 11 } } },
            { "update", new BsonDocument { { "$inc", new BsonDocument { { "count", 3 } } } } },
            { "new", true }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        var resultDoc = result["value"].AsBsonDocument;

        Assert.Equal(8, resultDoc["count"].AsInt32);
    }

    [Fact]
    public async Task FindAndModify_WithUpsert_Inserts()
    {
        var cmd = new BsonDocument
        {
            { "findandmodify", "test" },
            { "query", new BsonDocument { { "_id", 20 } } },
            { "update", new BsonDocument { { "$set", new BsonDocument { { "name", "upserted" }, { "count", 1 } } } } },
            { "upsert", true }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        Assert.Equal(BsonType.Null, result["value"].BsonType);

        var inserted = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 20)).FirstAsync();
        Assert.Equal("upserted", inserted["name"].AsString);
        Assert.Equal(1, inserted["count"].AsInt32);
    }

    [Fact]
    public async Task FindAndModify_WithUpsert_ReturnsPostImage()
    {
        var cmd = new BsonDocument
        {
            { "findandmodify", "test" },
            { "query", new BsonDocument { { "_id", 21 } } },
            { "update", new BsonDocument { { "$set", new BsonDocument { { "name", "upserted" }, { "count", 1 } } } } },
            { "upsert", true },
            { "new", true }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        var resultDoc = result["value"].AsBsonDocument;

        Assert.Equal("upserted", resultDoc["name"].AsString);
        Assert.Equal(1, resultDoc["count"].AsInt32);
    }

    [Fact]
    public async Task FindAndModify_NoMatch_WithoutUpsert()
    {
        var cmd = new BsonDocument
        {
            { "findandmodify", "test" },
            { "query", new BsonDocument { { "_id", 30 } } },
            { "update", new BsonDocument { { "$set", new BsonDocument { { "name", "test" } } } } }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        Assert.Equal(BsonType.Null, result["value"].BsonType);
    }

    // listCollections filter tests
    [Fact]
    public async Task ListCollections_WithFilter_ReturnsFiltered()
    {
        // Insert a document into the existing collection
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 100 } });

        var cmd = new BsonDocument
        {
            { "listCollections", 1 },
            { "filter", new BsonDocument { { "name", "test" } } }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        var collections = result["cursor"]["firstBatch"].AsBsonArray;

        Assert.Single(collections);
        Assert.Equal("test", collections[0].AsBsonDocument["name"].AsString);
    }

    [Fact]
    public async Task ListCollections_WithRegexFilter()
    {
        await _collection!.InsertOneAsync(new BsonDocument { { "_id", 100 } });

        var cmd = new BsonDocument
        {
            { "listCollections", 1 },
            { "filter", new BsonDocument { { "name", new BsonRegularExpression("^test") } } }
        };

        var result = await _database!.RunCommandAsync<BsonDocument>(cmd);
        var collections = result["cursor"]["firstBatch"].AsBsonArray;

        Assert.Single(collections);
        Assert.Equal("test", collections[0].AsBsonDocument["name"].AsString);
    }
}
