namespace Mongo.Fakes.Server.Auth;

/// <summary>Per-connection SCRAM/authentication state. One instance per <see cref="ClientConnection"/>.</summary>
internal sealed class AuthState
{
    public bool Authenticated { get; set; }

    public int ConversationId { get; set; }

    public ScramSha256Conversation? Conversation { get; set; }
}
