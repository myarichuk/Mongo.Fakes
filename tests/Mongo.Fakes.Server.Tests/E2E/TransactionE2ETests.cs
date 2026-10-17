using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server.Tests.TestSupport;

namespace Mongo.Fakes.Server.Tests.E2E;

public class TransactionE2ETests : IAsyncLifetime
{
    private FakeServer _fake = null!;
    private IMongoClient _client = null!;
    private IMongoCollection<BsonDocument> _items = null!;

    public async Task InitializeAsync()
    {
        _fake = await FakeServer.StartAsync(new MongoFakeServerOptions { ReplicaSetName = "rs0" });
        _client = _fake.CreateClient();
        _items = _client.GetDatabase("testdb").GetCollection<BsonDocument>("items");
    }

    public async Task DisposeAsync() => await _fake.DisposeAsync();

    [Fact]
    public async Task Hello_ReportsReplicaSetPrimary()
    {
        var hello = await _client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));

        Assert.Equal("rs0", hello["setName"].AsString);
        Assert.True(hello["isWritablePrimary"].AsBoolean);
        Assert.Equal($"127.0.0.1:{_fake.Server.Port}", hello["primary"].AsString);
        Assert.Equal(hello["primary"], hello["me"]);
        Assert.True(hello.Contains("topologyVersion"));
    }

    [Fact]
    public async Task CommitTransaction_Succeeds()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        await _items.InsertOneAsync(session, new BsonDocument("_id", 1));
        await _items.UpdateOneAsync(session, Builders<BsonDocument>.Filter.Eq("_id", 1), Builders<BsonDocument>.Update.Set("x", 1));
        await session.CommitTransactionAsync();

        var doc = await _items.Find(Builders<BsonDocument>.Filter.Eq("_id", 1)).SingleAsync();
        Assert.Equal(1, doc["x"].AsInt32);
    }

    [Fact]
    public async Task AbortTransaction_Succeeds()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        await _items.InsertOneAsync(session, new BsonDocument("_id", 2));
        await session.AbortTransactionAsync();

        Assert.False(session.IsInTransaction);
    }

    [Fact]
    public async Task WithTransaction_RunsTheCallback()
    {
        using var session = await _client.StartSessionAsync();
        var result = await session.WithTransactionAsync(async (s, ct) =>
        {
            await _items.InsertOneAsync(s, new BsonDocument("_id", 3), cancellationToken: ct);
            return "done";
        });

        Assert.Equal("done", result);
        Assert.Equal(1, await _items.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", 3)));
    }

    [Fact]
    public async Task SequentialTransactions_OnOneSession()
    {
        using var session = await _client.StartSessionAsync();
        for (int i = 10; i < 13; i++)
        {
            session.StartTransaction();
            await _items.InsertOneAsync(session, new BsonDocument("_id", i));
            await session.CommitTransactionAsync();
        }

        Assert.Equal(3, await _items.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task OperationOutsideRunningTransaction_IsNoSuchTransaction()
    {
        var lsid = new BsonDocument("id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard));
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _client.GetDatabase("testdb").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "insert", "items" },
            { "documents", new BsonArray { new BsonDocument("_id", 99) } },
            { "lsid", lsid },
            { "txnNumber", 5L },
            { "autocommit", false }
        }));

        Assert.Equal(251, ex.Code);
        Assert.True(ex.HasErrorLabel("TransientTransactionError"));
    }

    [Fact]
    public async Task Standalone_RefusesTransactions()
    {
        await using var standalone = await FakeServer.StartAsync();
        var client = standalone.CreateClient();
        using var session = await client.StartSessionAsync();
        session.StartTransaction();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetDatabase("testdb").GetCollection<BsonDocument>("items").InsertOneAsync(session, new BsonDocument("_id", 1)));
    }
}
