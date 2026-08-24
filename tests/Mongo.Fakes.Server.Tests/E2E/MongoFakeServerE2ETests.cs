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
}
