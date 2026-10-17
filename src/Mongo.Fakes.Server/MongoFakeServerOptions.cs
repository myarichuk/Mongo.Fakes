namespace Mongo.Fakes.Server;

/// <summary>Behaviour switches for <see cref="MongoFakeServer"/>.</summary>
public sealed class MongoFakeServerOptions
{
    /// <summary>
    /// How long a cursor may sit idle between batches before the server reaps it and a later
    /// <c>getMore</c> fails with CursorNotFound (43). A real server's default is 10 minutes;
    /// set it short to test cursor expiry. Cursors opened with <c>noCursorTimeout</c> are exempt.
    /// </summary>
    public TimeSpan CursorTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When set, the server reports itself as the primary of a single-node replica set with this
    /// name instead of a standalone server, so the driver allows transactions and retryable writes.
    /// </summary>
    public string? ReplicaSetName { get; init; }

    /// <summary>The host the server reports in <c>hello</c> (<c>me</c>, <c>hosts</c>, <c>primary</c>).</summary>
    public string AdvertisedHost { get; init; } = "127.0.0.1";
}
