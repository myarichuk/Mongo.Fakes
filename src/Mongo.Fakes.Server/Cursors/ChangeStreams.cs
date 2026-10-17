using MongoDB.Bson;
using Mongo.Fakes.Server.Aggregation;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server.Cursors;

/// <summary>One write, as recorded for change streams. Shaped per stream when delivered.</summary>
internal sealed record ChangeEvent(
    long Sequence,
    BsonTimestamp ClusterTime,
    DateTime WallTime,
    string OperationType,
    string Database,
    string? Collection,
    BsonValue? DocumentId,
    BsonDocument? FullDocument,
    BsonDocument? FullDocumentBeforeChange,
    BsonDocument? UpdateDescription,
    BsonDocument? To);

/// <summary>
/// The backend's write log for change streams: every write is appended in commit order, and
/// change stream cursors read it from their resume point. Keeps the last <see cref="Capacity"/>
/// events; a stream that falls further behind fails with ChangeStreamHistoryLost (286).
/// </summary>
internal sealed class ChangeEventHub
{
    public const int Capacity = 100_000;
    private const int ChangeStreamHistoryLost = 286;

    private readonly object _lock = new();
    // Sequences are contiguous, so the event with sequence s sits at index s - _firstSequence.
    private readonly List<ChangeEvent> _log = new();
    private long _firstSequence = 1;
    private long _sequence;
    private TaskCompletionSource _published = NewSignal();

    public static BsonDocument Token(long sequence) => new("_data", sequence.ToString("X16"));

    public static long ParseToken(BsonValue token)
    {
        if (token is BsonDocument doc && doc.TryGetValue("_data", out var data) && data.IsString
            && long.TryParse(data.AsString, System.Globalization.NumberStyles.HexNumber, null, out var sequence))
            return sequence;
        throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "invalid resume token");
    }

    private volatile bool _enabled;

    /// <summary>
    /// Starts recording writes. Called when the first change stream opens, so that a backend no
    /// stream ever watches doesn't keep a log of its writes.
    /// </summary>
    public void Enable() => _enabled = true;

    public long CurrentSequence
    {
        get
        {
            lock (_lock)
                return _sequence;
        }
    }

    public void Publish(string operationType, string database, string? collection, BsonValue? documentId = null,
        BsonDocument? fullDocument = null, BsonDocument? fullDocumentBeforeChange = null,
        BsonDocument? updateDescription = null, BsonDocument? to = null)
    {
        if (!_enabled)
            return;

        TaskCompletionSource signal;
        lock (_lock)
        {
            long sequence = ++_sequence;
            var now = DateTime.UtcNow;
            var clusterTime = new BsonTimestamp((int)new DateTimeOffset(now).ToUnixTimeSeconds(), (int)(sequence & int.MaxValue));
            _log.Add(new ChangeEvent(sequence, clusterTime, now, operationType, database, collection, documentId,
                fullDocument, fullDocumentBeforeChange, updateDescription, to));

            // Trim in chunks so appends stay amortised O(1).
            if (_log.Count > Capacity + Capacity / 4)
            {
                int drop = _log.Count - Capacity;
                _log.RemoveRange(0, drop);
                _firstSequence += drop;
            }

            signal = _published;
            _published = NewSignal();
        }
        signal.TrySetResult();
    }

    /// <summary>
    /// The events after <paramref name="afterSequence"/> that pass <paramref name="filter"/>, up to
    /// <paramref name="max"/>, plus the sequence read up to (to resume from) and a task that
    /// completes on the next publish.
    /// </summary>
    public (List<ChangeEvent> Events, long ReadUpTo, Task Next) Read(long afterSequence, Func<ChangeEvent, bool> filter, int max)
    {
        lock (_lock)
        {
            if (afterSequence < _firstSequence - 1)
                throw new MongoCommandException(ChangeStreamHistoryLost, "ChangeStreamHistoryLost",
                    "Resume of change stream was not possible, as the resume point may no longer be in the oplog.");

            var events = new List<ChangeEvent>();
            long readUpTo = Math.Max(afterSequence, _firstSequence - 1);
            for (int i = (int)(readUpTo + 1 - _firstSequence); i < _log.Count && events.Count < max; i++)
            {
                var e = _log[i];
                readUpTo = e.Sequence;
                if (filter(e))
                    events.Add(e);
            }

            return (events, readUpTo, _published.Task);
        }
    }

    /// <summary>The last sequence before <paramref name="time"/>, for <c>startAtOperationTime</c>.</summary>
    public long SequenceBefore(BsonTimestamp time)
    {
        lock (_lock)
        {
            long result = _firstSequence - 1;
            foreach (var e in _log)
            {
                if (e.ClusterTime.CompareTo(time) >= 0)
                    break;
                result = e.Sequence;
            }
            return result;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A <c>$changeStream</c> cursor. Each <c>getMore</c> returns the events written since the last
/// batch, waiting up to the getMore's <c>maxTimeMS</c> (the driver's maxAwaitTime) for one to arrive.
/// </summary>
internal sealed class ChangeStreamCursor : ServerCursor
{
    private const int DefaultBatchSize = 101;

    private readonly ChangeEventHub _hub;
    private readonly string? _collectionFilter;
    private readonly string? _databaseFilter;
    private readonly bool _includeFullDocumentOnUpdate;
    private readonly bool _includeBeforeChange;
    private readonly BsonArray _pipeline;
    private long _position;
    private bool _invalidated;

    private ChangeStreamCursor(ChangeEventHub hub, string database, string collection, int ownerConnectionId,
        string? databaseFilter, string? collectionFilter, BsonDocument options, BsonArray pipeline)
        : base(database, collection, ownerConnectionId, noCursorTimeout: false)
    {
        _hub = hub;
        _databaseFilter = databaseFilter;
        _collectionFilter = collectionFilter;
        _pipeline = pipeline;

        string fullDocument = options.TryGetValue("fullDocument", out var fd) && fd.IsString ? fd.AsString : "default";
        _includeFullDocumentOnUpdate = fullDocument is "updateLookup" or "whenAvailable" or "required";

        string before = options.TryGetValue("fullDocumentBeforeChange", out var fb) && fb.IsString ? fb.AsString : "off";
        _includeBeforeChange = before is "whenAvailable" or "required";

        if (options.TryGetValue("resumeAfter", out var resumeAfter))
            _position = ChangeEventHub.ParseToken(resumeAfter);
        else if (options.TryGetValue("startAfter", out var startAfter))
            _position = ChangeEventHub.ParseToken(startAfter);
        else if (options.TryGetValue("startAtOperationTime", out var startAt) && startAt is BsonTimestamp ts)
            _position = hub.SequenceBefore(ts);
        else
            _position = hub.CurrentSequence;
    }

    /// <summary>Opens a stream for <c>aggregate</c> with a leading <c>$changeStream</c> stage.</summary>
    public static ChangeStreamCursor Open(ChangeEventHub hub, ConnectionContext connection, string database, BsonValue aggregateTarget, BsonDocument options, BsonArray remainingPipeline)
    {
        bool allChangesForCluster = options.TryGetValue("allChangesForCluster", out var all) && all.ToBoolean();

        if (aggregateTarget.IsString)
            return new ChangeStreamCursor(hub, database, aggregateTarget.AsString, connection.ConnectionId, database, aggregateTarget.AsString, options, remainingPipeline);

        if (allChangesForCluster)
        {
            if (database != "admin")
                throw new MongoCommandException(ErrorCodes.InvalidNamespace, "InvalidNamespace",
                    "$changeStream may only be run with allChangesForCluster on the admin database");
            return new ChangeStreamCursor(hub, database, "$cmd.aggregate", connection.ConnectionId, null, null, options, remainingPipeline);
        }

        return new ChangeStreamCursor(hub, database, "$cmd.aggregate", connection.ConnectionId, database, null, options, remainingPipeline);
    }

    public BsonDocument PostBatchResumeToken => ChangeEventHub.Token(_position);

    public override async Task<CursorBatch> NextBatchAsync(int? batchSize, TimeSpan? maxAwaitTime, CancellationToken ct)
    {
        int max = batchSize is > 0 ? batchSize.Value : DefaultBatchSize;
        var deadline = maxAwaitTime.HasValue ? DateTime.UtcNow + maxAwaitTime.Value : DateTime.UtcNow;

        while (true)
        {
            var batch = ReadAvailable(max, out var next);
            if (batch.Count > 0 || _invalidated || DateTime.UtcNow >= deadline)
                return new CursorBatch(batch, _invalidated, PostBatchResumeToken);

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new CursorBatch(batch, false, PostBatchResumeToken);

            try
            {
                await next.WaitAsync(remaining, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    /// <summary>The events available now, without waiting, for the aggregate's first batch.</summary>
    public CursorBatch FirstBatch() => new(ReadAvailable(DefaultBatchSize, out _), _invalidated, PostBatchResumeToken);

    private BsonArray ReadAvailable(int max, out Task next)
    {
        var batch = new BsonArray();
        if (_invalidated)
        {
            next = Task.CompletedTask;
            return batch;
        }

        var (events, readUpTo, nextTask) = _hub.Read(_position, InScope, max);
        next = nextTask;
        _position = readUpTo;

        foreach (var e in events)
        {
            foreach (var doc in Shape(e))
                batch.Add(doc);

            // A collection stream ends after its collection is dropped or renamed.
            if (_collectionFilter != null && e.OperationType is "drop" or "rename")
            {
                batch.Add(new BsonDocument
                {
                    { "_id", ChangeEventHub.Token(e.Sequence) },
                    { "operationType", "invalidate" },
                    { "clusterTime", e.ClusterTime },
                    { "wallTime", e.WallTime }
                });
                _position = e.Sequence;
                _invalidated = true;
                break;
            }
        }

        return batch;
    }

    private bool InScope(ChangeEvent e)
    {
        if (_databaseFilter != null && e.Database != _databaseFilter)
            return false;
        if (_collectionFilter != null && e.Collection != _collectionFilter && e.OperationType != "dropDatabase")
            return false;
        return true;
    }

    private IEnumerable<BsonDocument> Shape(ChangeEvent e)
    {
        var doc = new BsonDocument
        {
            { "_id", ChangeEventHub.Token(e.Sequence) },
            { "operationType", e.OperationType },
            { "clusterTime", e.ClusterTime },
            { "wallTime", e.WallTime },
            { "ns", e.Collection != null
                ? new BsonDocument { { "db", e.Database }, { "coll", e.Collection } }
                : new BsonDocument("db", e.Database) }
        };

        if (e.DocumentId != null)
            doc["documentKey"] = new BsonDocument("_id", e.DocumentId);

        bool fullDocument = e.OperationType is "insert" or "replace"
            || (e.OperationType == "update" && _includeFullDocumentOnUpdate);
        if (fullDocument && e.FullDocument != null)
            doc["fullDocument"] = e.FullDocument;

        if (_includeBeforeChange && e.FullDocumentBeforeChange != null)
            doc["fullDocumentBeforeChange"] = e.FullDocumentBeforeChange;

        if (e.UpdateDescription != null)
            doc["updateDescription"] = e.UpdateDescription;

        if (e.To != null)
            doc["to"] = e.To;

        if (_pipeline.Count == 0)
            return [doc];

        return new AggregationPipeline().Execute([doc], _pipeline).ToList();
    }
}
