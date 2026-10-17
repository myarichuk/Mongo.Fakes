using MongoDB.Bson;
using Mongo.Fakes.Server.Aggregation;
using Mongo.Fakes.Server.Auth;
using Mongo.Fakes.Server.Cursors;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Protocol;

namespace Mongo.Fakes.Server;

internal sealed class CommandRouter
{
    private const int ChangeStreamNotSupported = 40573;

    private readonly IMongoBackend _backend;
    private readonly ScramCredential? _credential;
    private readonly MongoFakeServerOptions _options;
    private readonly SessionManager _sessionManager = new();
    private readonly FailPoints _failPoints = new();
    private int _conversationIdCounter;

    public CommandRouter(IMongoBackend backend, ScramCredential? credential = null, MongoFakeServerOptions? options = null)
    {
        _backend = backend;
        _credential = credential;
        _options = options ?? new MongoFakeServerOptions();
        Cursors = new CursorManager(_options.CursorTimeout);
    }

    /// <summary>The port the server listens on, for <c>hello</c> and <c>serverStatus</c>. Set once the listener starts.</summary>
    public int Port { get; set; }

    public ServerState State { get; } = new();

    public CursorManager Cursors { get; }

    public void OnConnectionClosed(ConnectionContext connection)
    {
        Cursors.KillConnectionCursors(connection.ConnectionId);
        State.CloseConnection();
    }

    public async Task<BsonDocument> RouteCommandAsync(string database, BsonDocument command, ConnectionContext connection, CancellationToken ct)
    {
        if (command.ElementCount == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Empty command document.");

        string commandName = command.GetElement(0).Name.ToLowerInvariant();
        State.CountCommand(commandName, command);
        long opId = State.BeginOperation(connection, database, command);
        try
        {
            BsonDocument? writeConcernError = null;
            if (commandName != "configurefailpoint")
            {
                var action = _failPoints.Evaluate(command.GetElement(0).Name, connection.AppName);
                if (action != null)
                    writeConcernError = await ApplyFailPointAsync(action, ct).ConfigureAwait(false);
            }

            CheckTransactionFields(commandName, command);

            var reply = await DispatchAsync(commandName, database, command, connection, ct).ConfigureAwait(false);

            if (writeConcernError != null)
            {
                reply["writeConcernError"] = writeConcernError;
                int code = writeConcernError.TryGetValue("code", out var c) ? c.ToInt32() : 0;
                var labels = ErrorLabels.For(code, command,
                    writeConcernError.TryGetValue("errorLabels", out var l) && l.IsBsonArray ? l.AsBsonArray.Select(v => v.AsString).ToList() : null);
                if (labels != null)
                    reply["errorLabels"] = labels;
            }

            return reply;
        }
        finally
        {
            State.EndOperation(opId);
        }
    }

    private async Task<BsonDocument> DispatchAsync(string commandName, string database, BsonDocument command, ConnectionContext connection, CancellationToken ct)
    {
        switch (commandName)
        {
            case "hello":
            case "ismaster":
                return HandleHello(command, connection);
            case "ping":
                return new BsonDocument { { "ok", 1.0 } };
            case "buildinfo":
                return HandleBuildInfo();
            case "getparameter":
                return new BsonDocument { { "ok", 1.0 } };
            case "endsessions":
                return HandleEndSessions(command);
            case "whatsmyuri":
                return new BsonDocument { { "you", connection.ClientAddress }, { "ok", 1.0 } };
            case "connectionstatus":
                return new BsonDocument { { "ok", 1.0 } };
            case "saslstart":
                return HandleSaslStart(command, connection.Auth);
            case "saslcontinue":
                return HandleSaslContinue(command, connection.Auth);
            case "startsession":
                return _sessionManager.StartSession();
            case "committransaction":
            case "aborttransaction":
                RequireAdmin(database, command);
                return new BsonDocument { { "ok", 1.0 } };
            case "configurefailpoint":
                RequireAdmin(database, command);
                return _failPoints.Configure(command);
            case "getmore":
                EnsureAuthenticated(connection);
                return await Cursors.GetMoreAsync(database, command, ct).ConfigureAwait(false);
            case "killcursors":
                EnsureAuthenticated(connection);
                return Cursors.KillCursors(database, command);
            case "serverstatus":
                EnsureAuthenticated(connection);
                return HandleServerStatus();
            case "aggregate" when FirstStageName(command) is "$currentOp" or "$changeStream":
                EnsureAuthenticated(connection);
                return FirstStageName(command) == "$currentOp"
                    ? HandleCurrentOp(database, command, connection)
                    : HandleChangeStream(database, command, connection);
            default:
                var reply = await ExecuteBackendCommandAsync(database, command, connection.Auth, ct).ConfigureAwait(false);
                SplitIntoBatches(commandName, command, reply, connection);
                return reply;
        }
    }

    /// <summary>Blocks, closes the connection or fails the command, as the failpoint says. Returns a writeConcernError to attach, if any.</summary>
    private static async Task<BsonDocument?> ApplyFailPointAsync(FailCommandAction action, CancellationToken ct)
    {
        if (action.BlockTime > TimeSpan.Zero)
            await Task.Delay(action.BlockTime, ct).ConfigureAwait(false);

        if (action.CloseConnection)
            throw new CloseConnectionException();

        if (action.ErrorCode.HasValue)
        {
            int code = action.ErrorCode.Value;
            throw new MongoCommandException(code, ErrorCodes.NameOf(code),
                action.ErrorMessage ?? "Failing command via 'failCommand' failpoint", errorLabels: action.ErrorLabels);
        }

        return action.WriteConcernError;
    }

    private void CheckTransactionFields(string commandName, BsonDocument command)
    {
        if (!command.Contains("txnNumber") && !command.Contains("autocommit"))
            return;

        if (_options.ReplicaSetName == null)
            throw new MongoCommandException(ErrorCodes.IllegalOperation, "IllegalOperation",
                "Transaction numbers are only allowed on a replica set member or mongos");

        _sessionManager.Apply(commandName, command);
    }

    /// <summary>
    /// The backend returns whole result sets in <c>firstBatch</c>; keep the first batch and leave the
    /// rest in a server-side cursor, honouring <c>batchSize</c>, <c>singleBatch</c> and <c>noCursorTimeout</c>.
    /// </summary>
    private void SplitIntoBatches(string commandName, BsonDocument command, BsonDocument reply, ConnectionContext connection)
    {
        int? batchSize;
        bool singleBatch = false;
        bool noCursorTimeout = false;

        switch (commandName)
        {
            case "find":
                batchSize = command.TryGetValue("batchSize", out var bs) && bs.IsNumeric ? bs.ToInt32() : null;
                singleBatch = (command.TryGetValue("singleBatch", out var sb) && sb.ToBoolean())
                    || (command.TryGetValue("limit", out var limit) && limit.IsNumeric && limit.ToInt64() < 0);
                noCursorTimeout = command.TryGetValue("noCursorTimeout", out var nct) && nct.ToBoolean();
                break;
            case "aggregate":
            case "listcollections":
            case "listindexes":
                batchSize = command.TryGetValue("cursor", out var c) && c is BsonDocument cursorOptions
                    && cursorOptions.TryGetValue("batchSize", out var cbs) && cbs.IsNumeric ? cbs.ToInt32() : null;
                break;
            default:
                return;
        }

        if (batchSize < 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "batchSize must be non-negative");

        Cursors.SplitFirstBatch(reply, connection, batchSize, singleBatch, noCursorTimeout);
    }

    private async Task<BsonDocument> ExecuteBackendCommandAsync(string database, BsonDocument command, AuthState authState, CancellationToken ct)
    {
        if (_credential != null && !authState.Authenticated)
            throw new MongoCommandException(ErrorCodes.Unauthorized, "Unauthorized", "command requires authentication");

        long maxTimeMS = GetMaxTimeMS(command);
        if (maxTimeMS == 0)
            return await _backend.ExecuteCommandAsync(database, command, ct).ConfigureAwait(false);

        // The backend checks the token as it scans, so a deadline interrupts a long-running command.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(maxTimeMS));
        try
        {
            return await _backend.ExecuteCommandAsync(database, command, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new MongoCommandException(ErrorCodes.MaxTimeMSExpired, "MaxTimeMSExpired", "operation exceeded time limit");
        }
    }

    private static long GetMaxTimeMS(BsonDocument command)
    {
        if (!command.TryGetValue("maxTimeMS", out var value))
            return 0;

        if (!value.IsNumeric)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "maxTimeMS must be a number");

        long ms = value.ToInt64();
        if (ms < 0 || ms > int.MaxValue)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", $"{ms} value for maxTimeMS is out of range [0, {int.MaxValue}]");

        return ms;
    }

    private void EnsureAuthenticated(ConnectionContext connection)
    {
        if (_credential != null && !connection.Auth.Authenticated)
            throw new MongoCommandException(ErrorCodes.Unauthorized, "Unauthorized", "command requires authentication");
    }

    private static void RequireAdmin(string database, BsonDocument command)
    {
        if (database != "admin")
            throw new MongoCommandException(ErrorCodes.Unauthorized, "Unauthorized",
                $"{command.GetElement(0).Name} may only be run against the admin database.");
    }

    private static string? FirstStageName(BsonDocument command)
    {
        if (command.TryGetValue("pipeline", out var pipeline) && pipeline is BsonArray stages
            && stages.Count > 0 && stages[0] is BsonDocument first && first.ElementCount > 0)
            return first.GetElement(0).Name;
        return null;
    }

    private BsonDocument HandleCurrentOp(string database, BsonDocument command, ConnectionContext connection)
    {
        if (database != "admin" || !command.GetElement(0).Value.IsNumeric)
            throw new MongoCommandException(ErrorCodes.InvalidNamespace, "InvalidNamespace",
                "$currentOp must be run against the 'admin' database with {aggregate: 1}");

        var pipeline = command["pipeline"].AsBsonArray;
        var ops = State.CurrentOps().ToList();
        var results = new AggregationPipeline().Execute(ops, new BsonArray(pipeline.Skip(1))).ToList();

        var reply = new BsonDocument
        {
            { "cursor", new BsonDocument
            {
                { "id", 0L },
                { "ns", "admin.$cmd.aggregate" },
                { "firstBatch", new BsonArray(results) }
            }},
            { "ok", 1.0 }
        };
        SplitIntoBatches("aggregate", command, reply, connection);
        return reply;
    }

    private BsonDocument HandleChangeStream(string database, BsonDocument command, ConnectionContext connection)
    {
        if (_options.ReplicaSetName == null)
            throw new MongoCommandException(ChangeStreamNotSupported, "Location40573",
                "The $changeStream stage is only supported on replica sets");

        if (_backend is not BsonFileBackend fileBackend)
            throw new MongoCommandException(ErrorCodes.CommandNotFound, "CommandNotFound", "this backend does not support change streams");

        var pipeline = command["pipeline"].AsBsonArray;
        var options = pipeline[0]["$changeStream"] as BsonDocument ?? new BsonDocument();
        var hub = fileBackend.ChangeEvents;
        hub.Enable();

        var cursor = ChangeStreamCursor.Open(hub, connection, database, command.GetElement(0).Value, options, new BsonArray(pipeline.Skip(1)));
        var batch = cursor.FirstBatch();
        long id = batch.Exhausted ? 0 : Cursors.Register(cursor);

        return new BsonDocument
        {
            { "cursor", new BsonDocument
            {
                { "firstBatch", batch.Documents },
                { "postBatchResumeToken", cursor.PostBatchResumeToken },
                { "id", id },
                { "ns", cursor.Namespace }
            }},
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleServerStatus()
    {
        var uptime = State.Uptime;
        var reply = new BsonDocument
        {
            { "host", $"{_options.AdvertisedHost}:{Port}" },
            { "version", HandshakeResponse.Version },
            { "process", "mongod" },
            { "pid", (long)Environment.ProcessId },
            { "uptime", Math.Floor(uptime.TotalSeconds) },
            { "uptimeMillis", (long)uptime.TotalMilliseconds },
            { "uptimeEstimate", (long)uptime.TotalSeconds },
            { "localTime", DateTime.UtcNow },
            { "connections", State.ConnectionsDocument() },
            { "network", State.NetworkDocument() },
            { "opcounters", State.OpCountersDocument() },
            { "mem", new BsonDocument { { "bits", 64 }, { "resident", 0 }, { "virtual", 0 } } },
            { "metrics", new BsonDocument("cursor", new BsonDocument("open", new BsonDocument("total", (long)Cursors.OpenCursorCount))) }
        };

        if (_options.ReplicaSetName != null)
        {
            string me = $"{_options.AdvertisedHost}:{Port}";
            reply["repl"] = new BsonDocument
            {
                { "setName", _options.ReplicaSetName },
                { "isWritablePrimary", true },
                { "secondary", false },
                { "primary", me },
                { "me", me },
                { "hosts", new BsonArray { me } }
            };
        }

        reply["ok"] = 1.0;
        return reply;
    }

    private BsonDocument HandleSaslStart(BsonDocument command, AuthState authState)
    {
        if (_credential == null)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Authentication is not enabled on this fake server.");

        if (!command.TryGetValue("payload", out var payloadValue) || !payloadValue.IsBsonBinaryData)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'payload' field.");

        var conversation = new ScramSha256Conversation(_credential);
        byte[] serverFirst = conversation.ProcessClientFirst(payloadValue.AsBsonBinaryData.Bytes);

        authState.Conversation = conversation;
        authState.ConversationId = Interlocked.Increment(ref _conversationIdCounter);

        return new BsonDocument
        {
            { "conversationId", authState.ConversationId },
            { "done", false },
            { "payload", new BsonBinaryData(serverFirst, BsonBinarySubType.Binary) }
        };
    }

    private BsonDocument HandleSaslContinue(BsonDocument command, AuthState authState)
    {
        if (_credential == null || authState.Conversation == null)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "No SCRAM conversation in progress.");

        if (!command.TryGetValue("conversationId", out var idValue) || idValue.ToInt32() != authState.ConversationId)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Unknown SCRAM conversation.");

        if (!command.TryGetValue("payload", out var payloadValue) || !payloadValue.IsBsonBinaryData)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'payload' field.");

        byte[] serverFinal = authState.Conversation.ProcessClientFinal(payloadValue.AsBsonBinaryData.Bytes);
        authState.Authenticated = true;
        authState.Conversation = null;

        return new BsonDocument
        {
            { "conversationId", authState.ConversationId },
            { "done", true },
            { "payload", new BsonBinaryData(serverFinal, BsonBinarySubType.Binary) }
        };
    }

    private BsonDocument HandleHello(BsonDocument command, ConnectionContext connection)
    {
        if (command.TryGetValue("client", out var client) && client is BsonDocument clientDoc
            && clientDoc.TryGetValue("application", out var app) && app is BsonDocument appDoc
            && appDoc.TryGetValue("name", out var name) && name.IsString)
            connection.AppName = name.AsString;

        var hello = HandshakeResponse.CreateHello(connection.ConnectionId, _options, Port, State.ProcessId);

        if (_credential != null && command.Contains("saslSupportedMechs"))
            hello["saslSupportedMechs"] = new BsonArray { "SCRAM-SHA-256" };

        return hello;
    }

    private static BsonDocument HandleBuildInfo()
    {
        return new BsonDocument
        {
            { "version", HandshakeResponse.Version },
            { "versionArray", new BsonArray { 6, 0, 0, 0 } },
            { "gitVersion", "fake" },
            { "maxBsonObjectSize", 16777216 },
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleEndSessions(BsonDocument command)
    {
        var sessionsValue = command.GetElement(0).Value;
        if (sessionsValue.IsBsonArray)
        {
            foreach (var sessionDoc in sessionsValue.AsBsonArray.OfType<BsonDocument>())
            {
                if (sessionDoc.TryGetValue("id", out var idValue) && idValue.IsBsonBinaryData)
                    _sessionManager.EndSession(idValue.AsBsonBinaryData);
            }
        }

        return new BsonDocument { { "ok", 1.0 } };
    }
}
