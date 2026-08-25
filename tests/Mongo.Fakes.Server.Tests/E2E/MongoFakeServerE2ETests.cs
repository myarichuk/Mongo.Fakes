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

    [Fact]
    public async Task Aggregate_Match_Should_Filter_Documents()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll5");
        var doc1 = new MongoDB.Bson.BsonDocument { { "_id", "doc1" }, { "category", "A" }, { "value", 10 } };
        var doc2 = new MongoDB.Bson.BsonDocument { { "_id", "doc2" }, { "category", "B" }, { "value", 20 } };
        var doc3 = new MongoDB.Bson.BsonDocument { { "_id", "doc3" }, { "category", "A" }, { "value", 30 } };

        await collection.InsertManyAsync(new[] { doc1, doc2, doc3 });

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() { { "$match", new MongoDB.Bson.BsonDocument { { "category", "A" } } } }
        };

        var cursor = await collection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var result = await cursor.ToListAsync();
        Assert.Equal(2, result.Count);
        Assert.True(result.All(d => d["category"].AsString == "A"));
    }

    [Fact]
    public async Task Aggregate_Sort_Skip_Limit_Should_Order_And_Paginate()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll6");
        var docs = Enumerable.Range(1, 5)
            .Select(i => new MongoDB.Bson.BsonDocument { { "_id", $"doc{i}" }, { "value", i } })
            .ToList();

        await collection.InsertManyAsync(docs);

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() { { "$sort", new MongoDB.Bson.BsonDocument { { "value", -1 } } } },
            new() { { "$skip", 1 } },
            new() { { "$limit", 2 } }
        };

        var cursor = await collection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var result = await cursor.ToListAsync();
        Assert.Equal(2, result.Count);
        Assert.Equal(4, result[0]["value"].AsInt32);
        Assert.Equal(3, result[1]["value"].AsInt32);
    }

    [Fact]
    public async Task Aggregate_Project_Should_Include_Selected_Fields()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll7");
        var doc = new MongoDB.Bson.BsonDocument { { "_id", "doc1" }, { "name", "Alice" }, { "secret", "hidden" }, { "age", 30 } };

        await collection.InsertOneAsync(doc);

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() { { "$project", new MongoDB.Bson.BsonDocument { { "name", 1 }, { "age", 1 } } } }
        };

        var cursor = await collection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var result = await cursor.FirstAsync();
        Assert.Equal("Alice", result["name"].AsString);
        Assert.Equal(30, result["age"].AsInt32);
        Assert.False(result.Contains("secret"));
    }

    [Fact]
    public async Task CountDocuments_Should_Use_Group_Sum_Pipeline()
    {
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("countcoll");
        await collection.InsertManyAsync(
        [
            new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "status", "active" } },
            new MongoDB.Bson.BsonDocument { { "_id", 2 }, { "status", "active" } },
            new MongoDB.Bson.BsonDocument { { "_id", 3 }, { "status", "inactive" } },
        ]);

        long total = await collection.CountDocumentsAsync(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Empty);
        Assert.Equal(3, total);

        long filtered = await collection.CountDocumentsAsync(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("status", "active"));
        Assert.Equal(2, filtered);

        long none = await collection.CountDocumentsAsync(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("status", "missing"));
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task Aggregate_Lookup_EqualityJoin_Should_Join_Collections()
    {
        var ordersCollection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("lookup_orders");
        var customersCollection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("lookup_customers");

        var orders = new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "customerId", "alice" }, { "item", "Widget" } },
            new MongoDB.Bson.BsonDocument { { "_id", 2 }, { "customerId", "bob" }, { "item", "Gadget" } },
            new MongoDB.Bson.BsonDocument { { "_id", 3 }, { "customerId", "alice" }, { "item", "Doohickey" } },
        };

        var customers = new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", "alice" }, { "name", "Alice Smith" }, { "age", 30 } },
            new MongoDB.Bson.BsonDocument { { "_id", "bob" }, { "name", "Bob Jones" }, { "age", 25 } },
        };

        await ordersCollection.InsertManyAsync(orders);
        await customersCollection.InsertManyAsync(customers);

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() {
                { "$lookup", new MongoDB.Bson.BsonDocument
                {
                    { "from", "lookup_customers" },
                    { "localField", "customerId" },
                    { "foreignField", "_id" },
                    { "as", "customer" }
                }}
            }
        };

        var cursor = await ordersCollection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var result = await cursor.ToListAsync();

        Assert.Equal(3, result.Count);

        // First order should have Alice's customer data
        var firstOrder = result[0];
        Assert.Equal("alice", firstOrder["customerId"].AsString);
        Assert.True(firstOrder.Contains("customer"));
        var customerArray = firstOrder["customer"].AsBsonArray;
        Assert.Equal(1, customerArray.Count);
        Assert.Equal("Alice Smith", customerArray[0]["name"].AsString);

        // Orders from same customer should have same customer data
        var aliceOrders = result.Where(o => o["customerId"].AsString == "alice").ToList();
        Assert.Equal(2, aliceOrders.Count);
        foreach (var order in aliceOrders)
        {
            Assert.Equal(1, order["customer"].AsBsonArray.Count);
            Assert.Equal("Alice Smith", order["customer"].AsBsonArray[0]["name"].AsString);
        }
    }

    [Fact]
    public async Task Aggregate_Lookup_SubPipeline_Should_Run_Pipeline_On_Foreign_Collection()
    {
        var ordersCollection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("lookup_orders_sub");
        var customersCollection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("lookup_customers_sub");

        var orders = new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "customerId", "alice" }, { "amount", 100 } },
            new MongoDB.Bson.BsonDocument { { "_id", 2 }, { "customerId", "bob" }, { "amount", 200 } },
        };

        var customers = new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", "alice" }, { "name", "Alice" }, { "credit", 500 } },
            new MongoDB.Bson.BsonDocument { { "_id", "bob" }, { "name", "Bob" }, { "credit", 300 } },
            new MongoDB.Bson.BsonDocument { { "_id", "charlie" }, { "name", "Charlie" }, { "credit", 1000 } },
        };

        await ordersCollection.InsertManyAsync(orders);
        await customersCollection.InsertManyAsync(customers);

        // Lookup with sub-pipeline that filters customers
        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() {
                { "$lookup", new MongoDB.Bson.BsonDocument
                {
                    { "from", "lookup_customers_sub" },
                    { "localField", "customerId" },
                    { "foreignField", "_id" },
                    { "pipeline", new MongoDB.Bson.BsonArray
                    {
                        new MongoDB.Bson.BsonDocument { { "$match", new MongoDB.Bson.BsonDocument { { "credit", new MongoDB.Bson.BsonDocument { { "$gte", 400 } } } } } }
                    }},
                    { "as", "customer" }
                }}
            }
        };

        var cursor = await ordersCollection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var result = await cursor.ToListAsync();

        // Only Alice (credit 500) should have a customer record; Bob (credit 300) should have empty array
        var aliceOrder = result.First(o => o["customerId"].AsString == "alice");
        Assert.Equal(1, aliceOrder["customer"].AsBsonArray.Count);
        Assert.Equal("Alice", aliceOrder["customer"].AsBsonArray[0]["name"].AsString);

        var bobOrder = result.First(o => o["customerId"].AsString == "bob");
        Assert.Equal(0, bobOrder["customer"].AsBsonArray.Count);
    }

    [Fact]
    public async Task Aggregate_Lookup_NonExistentCollection_Should_Return_EmptyArray()
    {
        var ordersCollection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("lookup_orders_nonexist");

        var orders = new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "customerId", "alice" } },
        };

        await ordersCollection.InsertManyAsync(orders);

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() {
                { "$lookup", new MongoDB.Bson.BsonDocument
                {
                    { "from", "nonexistent_collection" },
                    { "localField", "customerId" },
                    { "foreignField", "_id" },
                    { "as", "customer" }
                }}
            }
        };

        var cursor = await ordersCollection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var result = await cursor.ToListAsync();

        Assert.Equal(1, result.Count);
        Assert.True(result[0].Contains("customer"));
        Assert.Equal(0, result[0]["customer"].AsBsonArray.Count);
    }
}
