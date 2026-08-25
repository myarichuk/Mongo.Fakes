using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class SetWindowFieldsE2ETests : IAsyncLifetime
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
    public async Task SetWindowFields_DocumentNumber_AssignsSequentialNumbers()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window1");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "name", "Alice" } },
            new BsonDocument { { "_id", 2 }, { "name", "Bob" } },
            new BsonDocument { { "_id", 3 }, { "name", "Charlie" } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument { { "$sort", new BsonDocument { { "_id", 1 } } } },
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "output", new BsonDocument { { "docNum", new BsonDocument { { "$documentNumber", new BsonDocument() } } } } }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0]["docNum"].AsInt32);
        Assert.Equal(2, results[1]["docNum"].AsInt32);
        Assert.Equal(3, results[2]["docNum"].AsInt32);
    }

    [Fact]
    public async Task SetWindowFields_Rank_WithGaps()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window2");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "score", 100 } },
            new BsonDocument { { "_id", 2 }, { "score", 90 } },
            new BsonDocument { { "_id", 3 }, { "score", 90 } },
            new BsonDocument { { "_id", 4 }, { "score", 80 } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument { { "$sort", new BsonDocument { { "score", -1 } } } },
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "sortBy", new BsonDocument { { "score", -1 } } },
                        { "output", new BsonDocument { { "rank", new BsonDocument { { "$rank", new BsonDocument() } } } } }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(4, results.Count);
        Assert.Equal(1, results[0]["rank"].AsInt32); // score 100
        Assert.Equal(2, results[1]["rank"].AsInt32); // score 90
        Assert.Equal(2, results[2]["rank"].AsInt32); // score 90
        Assert.Equal(4, results[3]["rank"].AsInt32); // score 80 (gap)
    }

    [Fact]
    public async Task SetWindowFields_Partitioned()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window3");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "department", "Sales" }, { "salary", 50000 } },
            new BsonDocument { { "_id", 2 }, { "department", "Engineering" }, { "salary", 80000 } },
            new BsonDocument { { "_id", 3 }, { "department", "Sales" }, { "salary", 55000 } },
            new BsonDocument { { "_id", 4 }, { "department", "Engineering" }, { "salary", 90000 } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument { { "$sort", new BsonDocument { { "_id", 1 } } } },
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "partitionBy", "$department" },
                        { "output", new BsonDocument { { "docNum", new BsonDocument { { "$documentNumber", new BsonDocument() } } } } }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(4, results.Count);
        var salesDocs = results.Where(d => d["department"].AsString == "Sales").OrderBy(d => d["_id"].AsInt32).ToList();
        var engDocs = results.Where(d => d["department"].AsString == "Engineering").OrderBy(d => d["_id"].AsInt32).ToList();

        Assert.Equal(1, salesDocs[0]["docNum"].AsInt32);
        Assert.Equal(2, salesDocs[1]["docNum"].AsInt32);
        Assert.Equal(1, engDocs[0]["docNum"].AsInt32);
        Assert.Equal(2, engDocs[1]["docNum"].AsInt32);
    }

    [Fact]
    public async Task SetWindowFields_RunningSum()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window4");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "value", 10 } },
            new BsonDocument { { "_id", 2 }, { "value", 20 } },
            new BsonDocument { { "_id", 3 }, { "value", 30 } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument { { "$sort", new BsonDocument { { "_id", 1 } } } },
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "sortBy", new BsonDocument { { "_id", 1 } } },
                        {
                            "output", new BsonDocument
                            {
                                {
                                    "runningSum", new BsonDocument
                                    {
                                        { "$sum", "$value" },
                                        { "window", new BsonDocument { { "documents", new BsonArray { "unbounded", "current" } } } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal(10, results[0]["runningSum"].AsInt32);
        Assert.Equal(30, results[1]["runningSum"].AsInt32);
        Assert.Equal(60, results[2]["runningSum"].AsInt32);
    }

    [Fact]
    public async Task SetWindowFields_WindowMin()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window5");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "value", 30 } },
            new BsonDocument { { "_id", 2 }, { "value", 10 } },
            new BsonDocument { { "_id", 3 }, { "value", 20 } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "output", new BsonDocument { { "minValue", new BsonDocument { { "$min", "$value" } } } } }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, d => Assert.Equal(10, d["minValue"].AsInt32));
    }

    [Fact]
    public async Task SetWindowFields_WindowMax()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window6");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "value", 30 } },
            new BsonDocument { { "_id", 2 }, { "value", 10 } },
            new BsonDocument { { "_id", 3 }, { "value", 20 } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "output", new BsonDocument { { "maxValue", new BsonDocument { { "$max", "$value" } } } } }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, d => Assert.Equal(30, d["maxValue"].AsInt32));
    }

    [Fact]
    public async Task SetWindowFields_PreservesOriginalFields()
    {
        var db = _client!.GetDatabase("windowdb");
        var coll = db.GetCollection<BsonDocument>("window7");

        var docs = new[]
        {
            new BsonDocument { { "_id", 1 }, { "name", "Alice" }, { "score", 90 } }
        };
        await coll.InsertManyAsync(docs);

        var pipeline = new[]
        {
            new BsonDocument
            {
                {
                    "$setWindowFields", new BsonDocument
                    {
                        { "output", new BsonDocument { { "rank", new BsonDocument { { "$rank", new BsonDocument() } } } } }
                    }
                }
            }
        };

        var results = await coll.AggregateAsync<BsonDocument>(pipeline).Result.ToListAsync();

        Assert.Single(results);
        Assert.Equal(1, results[0]["_id"].AsInt32);
        Assert.Equal("Alice", results[0]["name"].AsString);
        Assert.Equal(90, results[0]["score"].AsInt32);
        Assert.Equal(1, results[0]["rank"].AsInt32);
    }
}
