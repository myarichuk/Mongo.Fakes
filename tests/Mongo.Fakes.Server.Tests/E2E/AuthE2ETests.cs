using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class AuthE2ETests : IAsyncLifetime
{
    private MongoFakeServer? _server;

    public async Task InitializeAsync()
    {
        var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
        _server = new MongoFakeServer(backend, port: 0, username: "testuser", password: "testpass");
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
            await _server.DisposeAsync();
    }

    private MongoClientSettings BuildSettings(string? username, string? password)
    {
        var settings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _server!.Port)
        };

        if (username != null)
        {
            settings.Credential = MongoCredential.CreateCredential("admin", username, password);
        }

        return settings;
    }

    [Fact]
    public async Task CorrectCredential_AuthenticatesAndAllowsDataCommands()
    {
        using var client = new MongoClient(BuildSettings("testuser", "testpass"));
        var collection = client.GetDatabase("testdb").GetCollection<BsonDocument>("authcoll");

        await collection.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "value", "ok" } });
        var found = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 1)).FirstAsync();

        Assert.Equal("ok", found["value"].AsString);
    }

    [Fact]
    public async Task WrongPassword_FailsAuthentication()
    {
        using var client = new MongoClient(BuildSettings("testuser", "wrong-password"));
        var collection = client.GetDatabase("testdb").GetCollection<BsonDocument>("authcoll");

        await Assert.ThrowsAsync<MongoAuthenticationException>(
            () => collection.InsertOneAsync(new BsonDocument { { "_id", 2 } }));
    }

    [Fact]
    public async Task NoCredential_IsRejectedForDataCommands()
    {
        using var client = new MongoClient(BuildSettings(null, null));
        var collection = client.GetDatabase("testdb").GetCollection<BsonDocument>("authcoll");

        await Assert.ThrowsAsync<MongoCommandException>(
            () => collection.InsertOneAsync(new BsonDocument { { "_id", 3 } }));
    }
}
