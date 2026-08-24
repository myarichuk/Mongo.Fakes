using Mongo2Go;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.Parity;

[Trait("Category", "RequiresMongod")]
public class FakeVsRealMongoTests : IAsyncLifetime
{
    private MongoDbRunner? _realMongoRunner;
    private MongoFakeServer? _fakeServer;
    private IMongoClient? _realClient;
    private IMongoClient? _fakeClient;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Mongo2Go v5.x requires Linux with glibc 2.35+");

        _realMongoRunner = MongoDbRunner.Start();
        _realClient = new MongoClient(_realMongoRunner.ConnectionString);

        var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
        _fakeServer = new MongoFakeServer(backend, port: 0);
        await _fakeServer.StartAsync();

        var fakeSettings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _fakeServer.Port)
        };
        _fakeClient = new MongoClient(fakeSettings);
    }

    public async Task DisposeAsync()
    {
        _fakeClient?.Dispose();
        if (_fakeServer != null)
            await _fakeServer.DisposeAsync();
        _realClient?.Dispose();
        _realMongoRunner?.Dispose();
    }

    private async Task<List<BsonDocument>> GetTestDocs()
    {
        return new()
        {
            BsonDocument.Parse("{ _id: 1, name: 'Alice', age: 30, category: 'A', value: 100 }"),
            BsonDocument.Parse("{ _id: 2, name: 'Bob', age: 25, category: 'B', value: 200 }"),
            BsonDocument.Parse("{ _id: 3, name: 'Charlie', age: 35, category: 'A', value: 150 }"),
            BsonDocument.Parse("{ _id: 4, name: 'Diana', age: 28, category: 'C', value: 120 }"),
            BsonDocument.Parse("{ _id: 5, name: 'Eve', age: 32, category: 'B', value: 180 }"),
        };
    }

    private async Task SeedCollections(
        IMongoCollection<BsonDocument> realColl,
        IMongoCollection<BsonDocument> fakeColl,
        List<BsonDocument> docs)
    {
        await realColl.InsertManyAsync(docs);
        await fakeColl.InsertManyAsync(docs);
    }

    private List<BsonDocument> NormalizeForComparison(List<BsonDocument> docs, string? sortField = null)
    {
        if (sortField != null)
            return docs.OrderBy(d => d[sortField].ToString()).ToList();
        return docs.OrderBy(d => d["_id"].AsInt32).ToList();
    }

    [Fact]
    public async Task Find_Empty_Filter_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll");

        await SeedCollections(realColl, fakeColl, testDocs);

        var realResults = (await realColl.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync())
            .OrderBy(d => d["_id"].AsInt32).ToList();
        var fakeResults = (await fakeColl.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync())
            .OrderBy(d => d["_id"].AsInt32).ToList();

        Assert.Equal(realResults.Count, fakeResults.Count);
        for (int i = 0; i < realResults.Count; i++)
            Assert.Equal(realResults[i].ToJson(), fakeResults[i].ToJson());
    }

    [Fact]
    public async Task Find_With_Equality_Filter_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll1");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll1");

        await SeedCollections(realColl, fakeColl, testDocs);

        var filter = Builders<BsonDocument>.Filter.Eq("category", "A");
        var realResults = (await realColl.Find(filter).ToListAsync()).OrderBy(d => d["_id"].AsInt32).ToList();
        var fakeResults = (await fakeColl.Find(filter).ToListAsync()).OrderBy(d => d["_id"].AsInt32).ToList();

        Assert.Equal(realResults.Count, fakeResults.Count);
        for (int i = 0; i < realResults.Count; i++)
            Assert.Equal(realResults[i].ToJson(), fakeResults[i].ToJson());
    }

    [Fact]
    public async Task Find_With_Sort_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll2");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll2");

        await SeedCollections(realColl, fakeColl, testDocs);

        var sort = Builders<BsonDocument>.Sort.Descending("age");
        var realResults = await realColl.Find(Builders<BsonDocument>.Filter.Empty).Sort(sort).ToListAsync();
        var fakeResults = await fakeColl.Find(Builders<BsonDocument>.Filter.Empty).Sort(sort).ToListAsync();

        Assert.Equal(realResults.Count, fakeResults.Count);
        for (int i = 0; i < realResults.Count; i++)
            Assert.Equal(realResults[i].ToJson(), fakeResults[i].ToJson());
    }

    [Fact]
    public async Task Find_With_Projection_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll3");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll3");

        await SeedCollections(realColl, fakeColl, testDocs);

        var projection = Builders<BsonDocument>.Projection.Include("name").Include("age");
        var realResults = (await realColl.Find(Builders<BsonDocument>.Filter.Empty)
            .Project(projection).ToListAsync()).OrderBy(d => d["_id"].AsInt32).ToList();
        var fakeResults = (await fakeColl.Find(Builders<BsonDocument>.Filter.Empty)
            .Project(projection).ToListAsync()).OrderBy(d => d["_id"].AsInt32).ToList();

        Assert.Equal(realResults.Count, fakeResults.Count);
        for (int i = 0; i < realResults.Count; i++)
        {
            var realDoc = realResults[i];
            var fakeDoc = fakeResults[i];
            Assert.True(realDoc.Contains("name"));
            Assert.True(fakeDoc.Contains("name"));
            Assert.Equal(realDoc["name"].AsString, fakeDoc["name"].AsString);
        }
    }

    [Fact]
    public async Task Find_With_Skip_Limit_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll4");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll4");

        await SeedCollections(realColl, fakeColl, testDocs);

        var realResults = (await realColl.Find(Builders<BsonDocument>.Filter.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Skip(1).Limit(2).ToListAsync());
        var fakeResults = (await fakeColl.Find(Builders<BsonDocument>.Filter.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Skip(1).Limit(2).ToListAsync());

        Assert.Equal(realResults.Count, fakeResults.Count);
        Assert.Equal(2, realResults.Count);
        Assert.Equal(2, realResults[0]["_id"].AsInt32);
        Assert.Equal(2, fakeResults[0]["_id"].AsInt32);
        Assert.Equal(3, realResults[1]["_id"].AsInt32);
        Assert.Equal(3, fakeResults[1]["_id"].AsInt32);
    }

    [Fact]
    public async Task Count_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll5");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll5");

        await SeedCollections(realColl, fakeColl, testDocs);

        var realCount = await realColl.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        var fakeCount = await fakeColl.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);

        Assert.Equal(realCount, fakeCount);
        Assert.Equal(5, realCount);
    }

    [Fact]
    public async Task Count_With_Filter_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll6");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll6");

        await SeedCollections(realColl, fakeColl, testDocs);

        var filter = Builders<BsonDocument>.Filter.Gte("age", 30);
        var realCount = await realColl.CountDocumentsAsync(filter);
        var fakeCount = await fakeColl.CountDocumentsAsync(filter);

        Assert.Equal(realCount, fakeCount);
        Assert.Equal(3, realCount);
    }

    [Fact]
    public async Task Aggregate_Match_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll7");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll7");

        await SeedCollections(realColl, fakeColl, testDocs);

        var pipeline = new[]
        {
            new BsonDocument { { "$match", new BsonDocument { { "category", "A" } } } }
        };

        var realCursor = await realColl.AggregateAsync<BsonDocument>(pipeline);
        var fakeCursor = await fakeColl.AggregateAsync<BsonDocument>(pipeline);

        var realResults = (await realCursor.ToListAsync()).OrderBy(d => d["_id"].AsInt32).ToList();
        var fakeResults = (await fakeCursor.ToListAsync()).OrderBy(d => d["_id"].AsInt32).ToList();

        Assert.Equal(2, realResults.Count);
        Assert.Equal(2, fakeResults.Count);
        Assert.Equal(realResults[0]["_id"].AsInt32, fakeResults[0]["_id"].AsInt32);
        Assert.Equal(realResults[1]["_id"].AsInt32, fakeResults[1]["_id"].AsInt32);
    }

    [Fact]
    public async Task Aggregate_Project_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll8");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll8");

        await SeedCollections(realColl, fakeColl, testDocs);

        var pipeline = new[]
        {
            new BsonDocument { { "$project", new BsonDocument { { "name", 1 }, { "age", 1 } } } },
            new BsonDocument { { "$sort", new BsonDocument { { "_id", 1 } } } }
        };

        var realCursor = await realColl.AggregateAsync<BsonDocument>(pipeline);
        var fakeCursor = await fakeColl.AggregateAsync<BsonDocument>(pipeline);

        var realResults = await realCursor.ToListAsync();
        var fakeResults = await fakeCursor.ToListAsync();

        Assert.Equal(realResults.Count, fakeResults.Count);
        for (int i = 0; i < realResults.Count; i++)
        {
            var realDoc = realResults[i];
            var fakeDoc = fakeResults[i];
            Assert.True(realDoc.Contains("name"));
            Assert.True(fakeDoc.Contains("name"));
            Assert.Equal(realDoc["name"].AsString, fakeDoc["name"].AsString);
            Assert.Equal(realDoc["age"].AsInt32, fakeDoc["age"].AsInt32);
        }
    }

    [Fact]
    public async Task Aggregate_Sort_Skip_Limit_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll9");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll9");

        await SeedCollections(realColl, fakeColl, testDocs);

        var pipeline = new[]
        {
            new BsonDocument { { "$sort", new BsonDocument { { "value", -1 } } } },
            new BsonDocument { { "$skip", 1 } },
            new BsonDocument { { "$limit", 2 } }
        };

        var realCursor = await realColl.AggregateAsync<BsonDocument>(pipeline);
        var fakeCursor = await fakeColl.AggregateAsync<BsonDocument>(pipeline);

        var realResults = await realCursor.ToListAsync();
        var fakeResults = await fakeCursor.ToListAsync();

        Assert.Equal(2, realResults.Count);
        Assert.Equal(2, fakeResults.Count);
        Assert.Equal(realResults[0]["_id"].AsInt32, fakeResults[0]["_id"].AsInt32);
        Assert.Equal(realResults[1]["_id"].AsInt32, fakeResults[1]["_id"].AsInt32);
        Assert.Equal(realResults[0]["value"].AsInt32, fakeResults[0]["value"].AsInt32);
        Assert.Equal(realResults[1]["value"].AsInt32, fakeResults[1]["value"].AsInt32);
    }

    [Fact]
    public async Task Insert_Should_Persist()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll10");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll10");

        await SeedCollections(realColl, fakeColl, testDocs);

        var newDoc = new BsonDocument { { "name", "Frank" }, { "age", 29 } };
        await realColl.InsertOneAsync(new BsonDocument(newDoc));
        await fakeColl.InsertOneAsync(new BsonDocument(newDoc));

        var realCount = await realColl.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        var fakeCount = await fakeColl.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);

        Assert.Equal(6, realCount);
        Assert.Equal(6, fakeCount);
    }

    [Fact]
    public async Task Update_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll11");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll11");

        await SeedCollections(realColl, fakeColl, testDocs);

        var filter = Builders<BsonDocument>.Filter.Eq("_id", 1);
        var replacement = new BsonDocument { { "name", "ALICE" }, { "age", 31 } };

        var realResult = await realColl.ReplaceOneAsync(filter, replacement);
        var fakeResult = await fakeColl.ReplaceOneAsync(filter, replacement);

        Assert.Equal(1, realResult.MatchedCount);
        Assert.Equal(1, fakeResult.MatchedCount);
        Assert.Equal(1, realResult.ModifiedCount);
        Assert.Equal(1, fakeResult.ModifiedCount);

        var realDoc = await realColl.Find(filter).FirstAsync();
        var fakeDoc = await fakeColl.Find(filter).FirstAsync();
        Assert.Equal("ALICE", realDoc["name"].AsString);
        Assert.Equal("ALICE", fakeDoc["name"].AsString);
    }

    [Fact]
    public async Task Delete_Should_Match()
    {
        var testDocs = await GetTestDocs();
        var realColl = _realClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll12");
        var fakeColl = _fakeClient!.GetDatabase("testdb").GetCollection<BsonDocument>("coll12");

        await SeedCollections(realColl, fakeColl, testDocs);

        var filter = Builders<BsonDocument>.Filter.Eq("_id", 1);
        var realResult = await realColl.DeleteOneAsync(filter);
        var fakeResult = await fakeColl.DeleteOneAsync(filter);

        Assert.Equal(1, realResult.DeletedCount);
        Assert.Equal(1, fakeResult.DeletedCount);

        var realCount = await realColl.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        var fakeCount = await fakeColl.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);

        Assert.Equal(4, realCount);
        Assert.Equal(4, fakeCount);
    }
}
