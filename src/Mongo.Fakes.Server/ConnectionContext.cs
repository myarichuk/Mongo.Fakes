using Mongo.Fakes.Server.Auth;

namespace Mongo.Fakes.Server;

/// <summary>Per-connection state the router needs. One instance per <see cref="ClientConnection"/>.</summary>
internal sealed class ConnectionContext
{
    public ConnectionContext(int connectionId, string clientAddress)
    {
        ConnectionId = connectionId;
        ClientAddress = clientAddress;
    }

    public int ConnectionId { get; }

    public string ClientAddress { get; }

    public AuthState Auth { get; } = new();

    /// <summary>The <c>client.application.name</c> the driver sent in its handshake, if any.</summary>
    public string? AppName { get; set; }
}
