using MongoDB.Driver;

namespace Mongo.Fakes.Server.Tests.TestSupport;

/// <summary>A started fake server over an in-memory baseline, and the clients connected to it.</summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly List<MongoClient> _clients = new();

    private FakeServer(MongoFakeServer server, InMemoryBaseline baseline)
    {
        Server = server;
        Baseline = baseline;
    }

    public MongoFakeServer Server { get; }

    public InMemoryBaseline Baseline { get; }

    public static async Task<FakeServer> StartAsync(MongoFakeServerOptions? options = null, InMemoryBaseline? baseline = null)
    {
        baseline ??= new InMemoryBaseline();
        var server = new MongoFakeServer(new BsonFileBackend(baseline), options ?? new MongoFakeServerOptions());
        await server.StartAsync();
        return new FakeServer(server, baseline);
    }

    public MongoClient CreateClient(Action<MongoClientSettings>? configure = null)
    {
        var settings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", Server.Port)
        };
        configure?.Invoke(settings);
        var client = new MongoClient(settings);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();
        await Server.DisposeAsync();
    }
}
