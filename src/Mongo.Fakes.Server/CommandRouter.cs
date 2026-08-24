using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Protocol;

namespace Mongo.Fakes.Server;

internal sealed class CommandRouter
{
    private readonly IMongoBackend _backend;

    public CommandRouter(IMongoBackend backend)
    {
        _backend = backend;
    }

    public async Task<BsonDocument> RouteCommandAsync(string database, BsonDocument command, CancellationToken ct)
    {
        if (command.ElementCount == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Empty command document.");

        string commandName = command.GetElement(0).Name.ToLowerInvariant();

        return commandName switch
        {
            "hello" => HandleHello(command),
            "ismaster" or "isMaster" => HandleHello(command),
            "ping" => HandlePing(),
            "buildinfo" => HandleBuildInfo(),
            "getparameter" => HandleGetParameter(command),
            "endsessions" => HandleEndSessions(),
            "killcursors" => HandleKillCursors(),
            "getmore" => HandleGetMore(),
            "whatsmyuri" => HandleWhatsMyUri(),
            "connectionstatus" => HandleConnectionStatus(),
            _ => await _backend.ExecuteCommandAsync(database, command, ct).ConfigureAwait(false)
        };
    }

    private BsonDocument HandleHello(BsonDocument command)
    {
        return HandshakeResponse.CreateHello();
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
            { "version", "4.4.0" },
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

    private BsonDocument HandleEndSessions()
    {
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
}
