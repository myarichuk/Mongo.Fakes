using System.Net.Sockets;
using Xunit;

namespace Mongo.Fakes.Server.Tests;

public class MongoFakeServerLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_Should_Not_Throw_While_Accept_Is_Pending()
    {
        for (int i = 0; i < 50; i++)
        {
            var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
            var server = new MongoFakeServer(backend, port: 0);
            await server.StartAsync();

            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_Should_Not_Throw_With_Connected_Client_Mid_Shutdown()
    {
        for (int i = 0; i < 50; i++)
        {
            var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
            var server = new MongoFakeServer(backend, port: 0);
            await server.StartAsync();

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);

            await server.DisposeAsync();
        }
    }
}
