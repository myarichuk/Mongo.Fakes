using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Bson;

namespace Mongo.Fakes.Server;

/// <summary>
/// Server-wide counters and the in-flight operations, for <c>serverStatus</c> and <c>$currentOp</c>.
/// </summary>
internal sealed class ServerState
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<long, ActiveOperation> _activeOperations = new();
    private long _nextOpId;
    private int _connectionIdCounter;
    private int _currentConnections;
    private long _totalConnections;
    private long _bytesIn;
    private long _bytesOut;
    private long _numRequests;
    private long _insert;
    private long _query;
    private long _update;
    private long _delete;
    private long _getMore;
    private long _command;

    public DateTime StartTime { get; } = DateTime.UtcNow;

    public ObjectId ProcessId { get; } = ObjectId.GenerateNewId();

    public TimeSpan Uptime => _uptime.Elapsed;

    public int OpenConnection()
    {
        Interlocked.Increment(ref _currentConnections);
        Interlocked.Increment(ref _totalConnections);
        return Interlocked.Increment(ref _connectionIdCounter);
    }

    public void CloseConnection() => Interlocked.Decrement(ref _currentConnections);

    public void RecordRequest(int bytesIn)
    {
        Interlocked.Increment(ref _numRequests);
        Interlocked.Add(ref _bytesIn, bytesIn);
    }

    public void RecordReply(int bytesOut) => Interlocked.Add(ref _bytesOut, bytesOut);

    /// <summary>Counts a command the way <c>opcounters</c> does: inserts by document, updates and deletes by statement.</summary>
    public void CountCommand(string commandName, BsonDocument command)
    {
        switch (commandName)
        {
            case "insert":
                Interlocked.Add(ref _insert, ArrayLength(command, "documents"));
                break;
            case "update":
                Interlocked.Add(ref _update, ArrayLength(command, "updates"));
                break;
            case "delete":
                Interlocked.Add(ref _delete, ArrayLength(command, "deletes"));
                break;
            case "find":
                Interlocked.Increment(ref _query);
                break;
            case "getmore":
                Interlocked.Increment(ref _getMore);
                break;
            default:
                Interlocked.Increment(ref _command);
                break;
        }
    }

    public long BeginOperation(ConnectionContext connection, string database, BsonDocument command)
    {
        long opId = Interlocked.Increment(ref _nextOpId);
        _activeOperations[opId] = new ActiveOperation(opId, connection, database, command, Stopwatch.StartNew());
        return opId;
    }

    public void EndOperation(long opId) => _activeOperations.TryRemove(opId, out _);

    public IEnumerable<BsonDocument> CurrentOps()
    {
        foreach (var op in _activeOperations.Values.OrderBy(o => o.OpId))
        {
            string commandName = op.Command.ElementCount > 0 ? op.Command.GetElement(0).Name : "";
            var collection = op.Command.ElementCount > 0 && op.Command[0].IsString ? op.Command[0].AsString : "$cmd";
            var doc = new BsonDocument
            {
                { "type", "op" },
                { "host", "fake" },
                { "desc", $"conn{op.Connection.ConnectionId}" },
                { "connectionId", op.Connection.ConnectionId },
                { "client", op.Connection.ClientAddress },
                { "active", true },
                { "opid", op.OpId },
                { "secs_running", (long)op.Elapsed.Elapsed.TotalSeconds },
                { "microsecs_running", (long)(op.Elapsed.Elapsed.TotalMilliseconds * 1000) },
                { "op", OpType(commandName) },
                { "ns", $"{op.Database}.{collection}" },
                { "command", op.Command }
            };
            if (op.Connection.AppName != null)
                doc["appName"] = op.Connection.AppName;
            yield return doc;
        }
    }

    public BsonDocument ConnectionsDocument() => new()
    {
        { "current", Volatile.Read(ref _currentConnections) },
        { "available", 1_000_000 - Volatile.Read(ref _currentConnections) },
        { "totalCreated", Interlocked.Read(ref _totalConnections) },
        { "active", _activeOperations.Values.Select(o => o.Connection.ConnectionId).Distinct().Count() }
    };

    public BsonDocument OpCountersDocument() => new()
    {
        { "insert", Interlocked.Read(ref _insert) },
        { "query", Interlocked.Read(ref _query) },
        { "update", Interlocked.Read(ref _update) },
        { "delete", Interlocked.Read(ref _delete) },
        { "getmore", Interlocked.Read(ref _getMore) },
        { "command", Interlocked.Read(ref _command) }
    };

    public BsonDocument NetworkDocument() => new()
    {
        { "bytesIn", Interlocked.Read(ref _bytesIn) },
        { "bytesOut", Interlocked.Read(ref _bytesOut) },
        { "numRequests", Interlocked.Read(ref _numRequests) }
    };

    private static long ArrayLength(BsonDocument command, string field) =>
        command.TryGetValue(field, out var value) && value.IsBsonArray ? value.AsBsonArray.Count : 0;

    private static string OpType(string commandName) => commandName.ToLowerInvariant() switch
    {
        "insert" => "insert",
        "update" => "update",
        "delete" => "remove",
        "find" => "query",
        "getmore" => "getmore",
        _ => "command"
    };

    private sealed record ActiveOperation(long OpId, ConnectionContext Connection, string Database, BsonDocument Command, Stopwatch Elapsed);
}
