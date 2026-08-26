using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class TextSearchE2ETests : IAsyncLifetime
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
    public async Task Find_WithTextSearch_FiltersDocuments()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search1");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("title")));

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "title", "hello world" } },
            new BsonDocument { { "_id", 2 }, { "title", "goodbye" } },
            new BsonDocument { { "_id", 3 }, { "title", "hello again" } }
        };
        await coll.InsertManyAsync(docs);

        var results = (await coll.Find(Builders<BsonDocument>.Filter.Text("hello")).ToListAsync())
            .OrderBy(d => d["_id"].AsInt32).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0]["_id"].AsInt32);
        Assert.Equal(3, results[1]["_id"].AsInt32);
    }

    [Fact]
    public async Task Find_WithTextSearchAndOtherFilter_CombinesFilters()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search2");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("title")));

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "title", "hello world" }, { "status", "active" } },
            new BsonDocument { { "_id", 2 }, { "title", "hello test" }, { "status", "inactive" } },
            new BsonDocument { { "_id", 3 }, { "title", "goodbye" }, { "status", "active" } }
        };
        await coll.InsertManyAsync(docs);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Text("hello"),
            Builders<BsonDocument>.Filter.Eq("status", "active")
        );
        var results = await coll.Find(filter).ToListAsync();

        Assert.Single(results);
        Assert.Equal(1, results[0]["_id"].AsInt32);
    }

    [Fact]
    public async Task Find_TextSearch_CaseInsensitive()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search3");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("content")));

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "content", "Hello World" } },
            new BsonDocument { { "_id", 2 }, { "content", "goodbye" } }
        };
        await coll.InsertManyAsync(docs);

        var results = (await coll.Find(Builders<BsonDocument>.Filter.Text("HELLO")).ToListAsync());

        Assert.Single(results);
        Assert.Equal(1, results[0]["_id"].AsInt32);
    }

    [Fact]
    public async Task Count_WithTextSearch_CountsMatches()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search4");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("text")));

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "text", "hello world" } },
            new BsonDocument { { "_id", 2 }, { "text", "goodbye" } },
            new BsonDocument { { "_id", 3 }, { "text", "hello again" } }
        };
        await coll.InsertManyAsync(docs);

        var count = await coll.CountDocumentsAsync(Builders<BsonDocument>.Filter.Text("hello"));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Aggregate_WithTextSearch_FiltersInMatch()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search5");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("content")));

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "content", "apple pie" } },
            new BsonDocument { { "_id", 2 }, { "content", "banana split" } },
            new BsonDocument { { "_id", 3 }, { "content", "apple crumble" } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument { { "$match", new BsonDocument { { "$text", new BsonDocument { { "$search", "apple" } } } } } },
            new BsonDocument { { "$sort", new BsonDocument { { "_id", 1 } } } }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0]["_id"].AsInt32);
        Assert.Equal(3, results[1]["_id"].AsInt32);
    }

    [Fact]
    public async Task Find_WithMetaTextScoreProjection_IsAdditiveNotRestrictive()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search6");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("title")));

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "title", "hello world" }, { "status", "active" } },
            new BsonDocument { { "_id", 2 }, { "title", "goodbye" }, { "status", "inactive" } }
        };
        await coll.InsertManyAsync(docs);

        var projection = new BsonDocument { { "score", new BsonDocument { { "$meta", "textScore" } } } };
        var results = await coll.Find(Builders<BsonDocument>.Filter.Text("hello"))
            .Project(projection)
            .ToListAsync();

        Assert.Single(results);
        var doc = results[0];
        Assert.True(doc.Contains("_id"));
        Assert.True(doc.Contains("title"));
        Assert.True(doc.Contains("status"));
        Assert.True(doc.Contains("score"));
    }

    [Fact]
    public async Task Aggregate_WithMetaOnlyProjectStage_IsAdditiveNotRestrictive()
    {
        var db = _client!.GetDatabase("textdb");
        var coll = db.GetCollection<BsonDocument>("search7");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Text("$**")));

        await coll.InsertManyAsync(new[]
        {
            new BsonDocument("Content", "blue ocean violin"),
            new BsonDocument("Content", "violin under the moon"),
        });

        var pipeline = new BsonDocument[]
        {
            new("$match", new BsonDocument("$text", new BsonDocument("$search", "violin"))),
            new("$project", new BsonDocument("score", new BsonDocument("$meta", "textScore"))),
        };

        var results = await coll.Aggregate<BsonDocument>(pipeline).ToListAsync();

        Assert.Equal(2, results.Count);
        foreach (var doc in results)
        {
            Assert.True(doc.Contains("_id"));
            Assert.True(doc.Contains("Content"));
            Assert.True(doc.Contains("score"));
        }
    }
}
