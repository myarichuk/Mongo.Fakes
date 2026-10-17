using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server.Tests.TestSupport;

namespace Mongo.Fakes.Server.Tests.E2E;

public class FailPointE2ETests : IAsyncLifetime
{
    private FakeServer _fake = null!;
    private IMongoClient _client = null!;
    private IMongoDatabase _admin = null!;
    private IMongoCollection<BsonDocument> _items = null!;

    public async Task InitializeAsync()
    {
        // Replica-set mode, so the driver sends txnNumber and retries writes.
        _fake = await FakeServer.StartAsync(new MongoFakeServerOptions { ReplicaSetName = "rs0" });
        _client = _fake.CreateClient();
        _admin = _client.GetDatabase("admin");
        _items = _client.GetDatabase("testdb").GetCollection<BsonDocument>("items");
    }

    public async Task DisposeAsync() => await _fake.DisposeAsync();

    private Task<BsonDocument> ConfigureAsync(BsonValue mode, BsonDocument data) =>
        _admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", mode }, { "data", data } });

    private static BsonDocument Times(int n) => new("times", n);

    [Fact]
    public async Task ErrorCode_FailsTheListedCommand()
    {
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "find" } }, { "errorCode", 2 } });

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _items.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        Assert.Equal(2, ex.Code);
        Assert.Equal("BadValue", ex.CodeName);

        // times: 1 is used up.
        await _items.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    }

    [Fact]
    public async Task AlwaysOn_UntilTurnedOff()
    {
        await ConfigureAsync("alwaysOn", new BsonDocument { { "failCommands", new BsonArray { "count" } }, { "errorCode", 8 } });

        await Assert.ThrowsAsync<MongoCommandException>(() => _admin.RunCommandAsync<BsonDocument>(new BsonDocument("count", "items")));
        await Assert.ThrowsAsync<MongoCommandException>(() => _admin.RunCommandAsync<BsonDocument>(new BsonDocument("count", "items")));

        var reply = await _admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        Assert.Equal(2, reply["count"].ToInt64());

        await _admin.RunCommandAsync<BsonDocument>(new BsonDocument("count", "items"));
    }

    [Fact]
    public async Task Skip_LetsTheFirstCommandsThrough()
    {
        await ConfigureAsync(new BsonDocument("skip", 2), new BsonDocument { { "failCommands", new BsonArray { "ping" } }, { "errorCode", 8 } });

        await _admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        await _admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        await Assert.ThrowsAsync<MongoCommandException>(() => _admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));
    }

    [Fact]
    public async Task RetryableError_IsRetriedByTheDriver()
    {
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "insert" } }, { "errorCode", 10107 } });

        await _items.InsertOneAsync(new BsonDocument("_id", 1));

        Assert.Equal(1, await _items.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task RetryableError_CarriesRetryableWriteErrorLabel()
    {
        var noRetry = _fake.CreateClient(s => s.RetryWrites = false).GetDatabase("testdb").GetCollection<BsonDocument>("items");
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "insert" } }, { "errorCode", 91 } });

        var ex = await Assert.ThrowsAnyAsync<MongoException>(() => noRetry.InsertOneAsync(new BsonDocument("_id", 2)));

        // Without retryable writes the driver sends no txnNumber, so the server attaches no label.
        Assert.False(ex.HasErrorLabel("RetryableWriteError"));

        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "insert" } }, { "errorCode", 91 }, { "errorLabels", new BsonArray { "RetryableWriteError" } } });
        var labelled = await Assert.ThrowsAnyAsync<MongoException>(() => noRetry.InsertOneAsync(new BsonDocument("_id", 3)));
        Assert.True(labelled.HasErrorLabel("RetryableWriteError"));
    }

    [Fact]
    public async Task ExplicitErrorLabels_ReplaceDerivedOnes()
    {
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "ping" } }, { "errorCode", 8 }, { "errorLabels", new BsonArray { "Custom" } } });

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));

        Assert.True(ex.HasErrorLabel("Custom"));
    }

    [Fact]
    public async Task CloseConnection_IsANetworkError()
    {
        var noRetry = _fake.CreateClient(s => s.RetryReads = false).GetDatabase("testdb").GetCollection<BsonDocument>("items");
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "find" } }, { "closeConnection", true } });

        await Assert.ThrowsAnyAsync<MongoConnectionException>(() => noRetry.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

        // The driver reconnects for the next operation.
        await noRetry.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    }

    [Fact]
    public async Task CloseConnection_IsRetriedForReads()
    {
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "find" } }, { "closeConnection", true } });

        await _items.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    }

    [Fact]
    public async Task BlockConnection_DelaysTheCommand()
    {
        await ConfigureAsync(Times(1), new BsonDocument { { "failCommands", new BsonArray { "ping" } }, { "blockConnection", true }, { "blockTimeMS", 300 } });

        var stopwatch = Stopwatch.StartNew();
        await _admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        Assert.True(stopwatch.ElapsedMilliseconds >= 280, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AppName_LimitsTheFailpointToOneClient()
    {
        var tagged = _fake.CreateClient(s => s.ApplicationName = "victim").GetDatabase("admin");
        await ConfigureAsync("alwaysOn", new BsonDocument { { "failCommands", new BsonArray { "ping" } }, { "errorCode", 8 }, { "appName", "victim" } });

        await _admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        await Assert.ThrowsAsync<MongoCommandException>(() => tagged.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));

        await ConfigureAsync("off", new BsonDocument());
    }

    [Fact]
    public async Task WriteConcernError_IsReportedAfterTheWrite()
    {
        await ConfigureAsync(Times(1), new BsonDocument
        {
            { "failCommands", new BsonArray { "insert" } },
            { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "waiting for replication timed out" } } }
        });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => _items.InsertOneAsync(new BsonDocument("_id", 9)));

        Assert.Equal(64, ex.WriteConcernError.Code);
        Assert.Equal(1, await _items.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task ConfigureFailPoint_RequiresAdmin()
    {
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => _client.GetDatabase("testdb").RunCommandAsync<BsonDocument>(
            new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } }));

        Assert.Equal(13, ex.Code);
    }
}
