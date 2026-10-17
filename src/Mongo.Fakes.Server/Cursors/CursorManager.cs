using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server.Cursors;

/// <summary>One batch from a cursor, and whether the cursor has nothing more to give.</summary>
internal sealed record CursorBatch(BsonArray Documents, bool Exhausted, BsonDocument? PostBatchResumeToken = null);

/// <summary>A server-side cursor: the part of a result set the client hasn't fetched yet.</summary>
internal abstract class ServerCursor
{
    protected ServerCursor(string database, string collection, int ownerConnectionId, bool noCursorTimeout)
    {
        Database = database;
        Collection = collection;
        OwnerConnectionId = ownerConnectionId;
        NoCursorTimeout = noCursorTimeout;
    }

    public long Id { get; set; }

    public string Database { get; }

    public string Collection { get; }

    public string Namespace => $"{Database}.{Collection}";

    public int OwnerConnectionId { get; }

    public bool NoCursorTimeout { get; }

    /// <summary>Milliseconds (<see cref="Environment.TickCount64"/>) when the cursor last served a batch.</summary>
    public long LastUsed { get; set; } = Environment.TickCount64;

    public abstract Task<CursorBatch> NextBatchAsync(int? batchSize, TimeSpan? maxAwaitTime, CancellationToken ct);

    public virtual void Close()
    {
    }
}

/// <summary>A cursor over a result set the backend already computed.</summary>
internal sealed class ListCursor : ServerCursor
{
    private readonly IReadOnlyList<BsonValue> _documents;
    private int _position;

    public ListCursor(string database, string collection, int ownerConnectionId, bool noCursorTimeout, IReadOnlyList<BsonValue> documents, int position)
        : base(database, collection, ownerConnectionId, noCursorTimeout)
    {
        _documents = documents;
        _position = position;
    }

    public override Task<CursorBatch> NextBatchAsync(int? batchSize, TimeSpan? maxAwaitTime, CancellationToken ct)
    {
        int remaining = _documents.Count - _position;
        int count = batchSize is > 0 ? Math.Min(batchSize.Value, remaining) : remaining;

        var batch = new BsonArray(count);
        for (int i = 0; i < count; i++)
            batch.Add(_documents[_position + i]);
        _position += count;

        return Task.FromResult(new CursorBatch(batch, _position >= _documents.Count));
    }
}

/// <summary>
/// The server's cursor table: splits results into batches, serves <c>getMore</c> and
/// <c>killCursors</c>, and reaps cursors that sit idle past the timeout.
/// </summary>
internal sealed class CursorManager
{
    /// <summary>A real server's first-batch size when the client doesn't ask for one.</summary>
    public const int DefaultFirstBatchSize = 101;

    private static readonly TimeSpan DefaultMaxAwaitTime = TimeSpan.FromSeconds(1);

    private readonly Dictionary<long, ServerCursor> _cursors = new();
    private readonly object _lock = new();
    private readonly TimeSpan _timeout;
    private long _nextId = Random.Shared.NextInt64(1L << 32, 1L << 52);

    public CursorManager(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public int OpenCursorCount
    {
        get
        {
            lock (_lock)
            {
                ReapExpired();
                return _cursors.Count;
            }
        }
    }

    /// <summary>
    /// Trims the reply's <c>cursor.firstBatch</c>, which holds the whole result set, to the
    /// first batch and keeps the rest in a new cursor whose id goes in the reply.
    /// </summary>
    public void SplitFirstBatch(BsonDocument reply, ConnectionContext connection, int? batchSize, bool singleBatch, bool noCursorTimeout)
    {
        if (!reply.TryGetValue("cursor", out var cursorValue) || cursorValue is not BsonDocument cursorDoc)
            return;
        if (!cursorDoc.TryGetValue("firstBatch", out var batchValue) || batchValue is not BsonArray all)
            return;
        if (cursorDoc.TryGetValue("id", out var idValue) && idValue.ToInt64() != 0)
            return;

        int firstBatchSize = batchSize ?? DefaultFirstBatchSize;
        if (all.Count <= firstBatchSize)
            return;

        var firstBatch = new BsonArray(all.Take(firstBatchSize));
        cursorDoc["firstBatch"] = firstBatch;

        if (singleBatch)
            return;

        var (database, collection) = SplitNamespace(cursorDoc["ns"].AsString);
        var cursor = new ListCursor(database, collection, connection.ConnectionId, noCursorTimeout, all.Values.ToList(), firstBatchSize);
        cursorDoc["id"] = Register(cursor);
    }

    public long Register(ServerCursor cursor)
    {
        lock (_lock)
        {
            ReapExpired();
            cursor.Id = ++_nextId;
            cursor.LastUsed = Environment.TickCount64;
            _cursors[cursor.Id] = cursor;
            return cursor.Id;
        }
    }

    public async Task<BsonDocument> GetMoreAsync(string database, BsonDocument command, CancellationToken ct)
    {
        var idValue = command.GetElement(0).Value;
        if (!idValue.IsInt64 && !idValue.IsInt32)
            throw new MongoCommandException(ErrorCodes.TypeMismatch, "TypeMismatch", "Field 'getMore' must be of type long");
        long id = idValue.ToInt64();

        if (!command.TryGetValue("collection", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Field 'collection' is missing");

        int? batchSize = command.TryGetValue("batchSize", out var bs) && bs.IsNumeric ? bs.ToInt32() : null;
        if (batchSize < 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "batchSize must be non-negative");

        TimeSpan? maxAwaitTime = command.TryGetValue("maxTimeMS", out var mt) && mt.IsNumeric
            ? TimeSpan.FromMilliseconds(mt.ToInt64())
            : null;

        ServerCursor cursor;
        lock (_lock)
        {
            ReapExpired();
            if (!_cursors.TryGetValue(id, out cursor!))
                throw new MongoCommandException(ErrorCodes.CursorNotFound, "CursorNotFound", $"cursor id {id} not found");

            string ns = $"{database}.{collValue.AsString}";
            if (cursor.Namespace != ns)
                throw new MongoCommandException(ErrorCodes.Unauthorized, "Unauthorized",
                    $"Requested getMore on namespace '{ns}', but cursor belongs to a different namespace {cursor.Namespace}");

            // Not reaped while a batch is being served.
            cursor.LastUsed = long.MaxValue;
        }

        CursorBatch batch;
        try
        {
            batch = await cursor.NextBatchAsync(batchSize, maxAwaitTime ?? DefaultMaxAwaitTime, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                cursor.LastUsed = Environment.TickCount64;
        }

        long replyId = cursor.Id;
        if (batch.Exhausted)
        {
            Remove(cursor.Id);
            replyId = 0;
        }

        var cursorDoc = new BsonDocument
        {
            { "id", replyId },
            { "ns", cursor.Namespace },
            { "nextBatch", batch.Documents }
        };
        if (batch.PostBatchResumeToken != null)
            cursorDoc["postBatchResumeToken"] = batch.PostBatchResumeToken;

        return new BsonDocument { { "cursor", cursorDoc }, { "ok", 1.0 } };
    }

    public BsonDocument KillCursors(string database, BsonDocument command)
    {
        if (!command.TryGetValue("cursors", out var cursorsValue) || !cursorsValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Field 'cursors' must be an array");

        string ns = command.GetElement(0).Value.IsString ? $"{database}.{command.GetElement(0).Value.AsString}" : string.Empty;
        var killed = new BsonArray();
        var notFound = new BsonArray();

        lock (_lock)
        {
            ReapExpired();
            foreach (var value in cursorsValue.AsBsonArray)
            {
                long id = value.ToInt64();
                if (_cursors.TryGetValue(id, out var cursor) && cursor.Namespace == ns)
                {
                    _cursors.Remove(id);
                    cursor.Close();
                    killed.Add(id);
                }
                else
                {
                    notFound.Add(id);
                }
            }
        }

        return new BsonDocument
        {
            { "cursorsKilled", killed },
            { "cursorsNotFound", notFound },
            { "cursorsAlive", new BsonArray() },
            { "cursorsUnknown", new BsonArray() },
            { "ok", 1.0 }
        };
    }

    /// <summary>Frees the cursors a connection opened, when that connection closes.</summary>
    public void KillConnectionCursors(int connectionId)
    {
        lock (_lock)
        {
            foreach (var cursor in _cursors.Values.Where(c => c.OwnerConnectionId == connectionId).ToList())
            {
                _cursors.Remove(cursor.Id);
                cursor.Close();
            }
        }
    }

    private void Remove(long id)
    {
        lock (_lock)
        {
            if (_cursors.Remove(id, out var cursor))
                cursor.Close();
        }
    }

    private void ReapExpired()
    {
        long now = Environment.TickCount64;
        long timeoutMs = (long)_timeout.TotalMilliseconds;
        List<ServerCursor>? expired = null;
        foreach (var cursor in _cursors.Values)
        {
            if (!cursor.NoCursorTimeout && cursor.LastUsed != long.MaxValue && now - cursor.LastUsed > timeoutMs)
                (expired ??= new()).Add(cursor);
        }

        if (expired == null)
            return;

        foreach (var cursor in expired)
        {
            _cursors.Remove(cursor.Id);
            cursor.Close();
        }
    }

    private static (string Database, string Collection) SplitNamespace(string ns)
    {
        int dot = ns.IndexOf('.');
        return dot < 0 ? (ns, string.Empty) : (ns[..dot], ns[(dot + 1)..]);
    }
}
