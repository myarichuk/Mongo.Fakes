using MongoDB.Bson;
using Mongo.Fakes.Server.Auth;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Protocol;

namespace Mongo.Fakes.Server;

internal sealed class CommandRouter
{
    private readonly IMongoBackend _backend;
    private readonly ScramCredential? _credential;
    private readonly SessionManager _sessionManager;
    private int _conversationIdCounter;

    public CommandRouter(IMongoBackend backend, ScramCredential? credential = null)
    {
        _backend = backend;
        _credential = credential;
        _sessionManager = new SessionManager();
    }

    public async Task<BsonDocument> RouteCommandAsync(string database, BsonDocument command, AuthState authState, CancellationToken ct)
    {
        if (command.ElementCount == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Empty command document.");

        string commandName = command.GetElement(0).Name.ToLowerInvariant();

        return commandName switch
        {
            "hello" => HandleHello(command),
            "ismaster" => HandleHello(command),
            "ping" => HandlePing(),
            "buildinfo" => HandleBuildInfo(),
            "getparameter" => HandleGetParameter(command),
            "endsessions" => HandleEndSessions(command),
            "killcursors" => HandleKillCursors(),
            "getmore" => HandleGetMore(),
            "whatsmyuri" => HandleWhatsMyUri(),
            "connectionstatus" => HandleConnectionStatus(),
            "saslstart" => HandleSaslStart(command, authState),
            "saslcontinue" => HandleSaslContinue(command, authState),
            "startsession" => HandleStartSession(),
            "begintransaction" => HandleBeginTransaction(command),
            "committransaction" => HandleCommitTransaction(command),
            "aborttransaction" => HandleAbortTransaction(command),
            _ => await ExecuteBackendCommandAsync(database, command, authState, ct).ConfigureAwait(false)
        };
    }

    private async Task<BsonDocument> ExecuteBackendCommandAsync(string database, BsonDocument command, AuthState authState, CancellationToken ct)
    {
        if (_credential != null && !authState.Authenticated)
            throw new MongoCommandException(ErrorCodes.Unauthorized, "Unauthorized", "command requires authentication");

        return await _backend.ExecuteCommandAsync(database, command, ct).ConfigureAwait(false);
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

    private BsonDocument HandleHello(BsonDocument command)
    {
        var hello = HandshakeResponse.CreateHello();

        if (_credential != null && command.Contains("saslSupportedMechs"))
            hello["saslSupportedMechs"] = new BsonArray { "SCRAM-SHA-256" };

        return hello;
    }

    private BsonDocument HandlePing()
    {
        return new BsonDocument
        {
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleBuildInfo()
    {
        return new BsonDocument
        {
            { "ok", 1.0 },
            { "version", "6.0.0" },
            { "gitVersion", "fake" }
        };
    }

    private BsonDocument HandleGetParameter(BsonDocument command)
    {
        return new BsonDocument
        {
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleEndSessions(BsonDocument command)
    {
        if (command.TryGetValue("sessions", out var sessionsValue) && sessionsValue.IsBsonArray)
        {
            foreach (var sessionDoc in sessionsValue.AsBsonArray.OfType<BsonDocument>())
            {
                if (sessionDoc.TryGetValue("id", out var idValue) && idValue.IsBsonBinaryData)
                {
                    _sessionManager.EndSession(idValue.AsBsonBinaryData);
                }
            }
        }

        return new BsonDocument
        {
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleKillCursors()
    {
        return new BsonDocument
        {
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleGetMore()
    {
        throw new MongoCommandException(ErrorCodes.CursorNotFound, "CursorNotFound", "cursor id not found");
    }

    private BsonDocument HandleWhatsMyUri()
    {
        return new BsonDocument
        {
            { "ok", 1.0 },
            { "you", "127.0.0.1:0" }
        };
    }

    private BsonDocument HandleConnectionStatus()
    {
        return new BsonDocument
        {
            { "ok", 1.0 }
        };
    }

    private BsonDocument HandleStartSession()
    {
        return _sessionManager.StartSession();
    }

    private BsonDocument HandleBeginTransaction(BsonDocument command)
    {
        if (!command.TryGetValue("lsid", out var lsidValue) || !lsidValue.IsBsonDocument)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'lsid' field.");

        var lsidDoc = lsidValue.AsBsonDocument;
        if (!lsidDoc.TryGetValue("id", out var idValue) || !idValue.IsBsonBinaryData)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing session id in lsid.");

        int? txnNumber = null;
        if (command.TryGetValue("txnNumber", out var txnValue) && txnValue.IsInt32)
            txnNumber = txnValue.ToInt32();

        return _sessionManager.BeginTransaction(idValue.AsBsonBinaryData, txnNumber);
    }

    private BsonDocument HandleCommitTransaction(BsonDocument command)
    {
        if (!command.TryGetValue("lsid", out var lsidValue) || !lsidValue.IsBsonDocument)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'lsid' field.");

        var lsidDoc = lsidValue.AsBsonDocument;
        if (!lsidDoc.TryGetValue("id", out var idValue) || !idValue.IsBsonBinaryData)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing session id in lsid.");

        return _sessionManager.CommitTransaction(idValue.AsBsonBinaryData);
    }

    private BsonDocument HandleAbortTransaction(BsonDocument command)
    {
        if (!command.TryGetValue("lsid", out var lsidValue) || !lsidValue.IsBsonDocument)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'lsid' field.");

        var lsidDoc = lsidValue.AsBsonDocument;
        if (!lsidDoc.TryGetValue("id", out var idValue) || !idValue.IsBsonBinaryData)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing session id in lsid.");

        return _sessionManager.AbortTransaction(idValue.AsBsonBinaryData);
    }
}
