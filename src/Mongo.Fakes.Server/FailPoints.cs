using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server;

/// <summary>What a triggered <c>failCommand</c> failpoint does to one command.</summary>
internal sealed record FailCommandAction(
    int? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string>? ErrorLabels,
    bool CloseConnection,
    TimeSpan BlockTime,
    BsonDocument? WriteConcernError);

/// <summary>Thrown when a failpoint asks for the connection to be closed instead of answered.</summary>
internal sealed class CloseConnectionException : Exception
{
}

/// <summary>
/// The <c>failCommand</c> failpoint, set with <c>configureFailPoint</c> as on a real server
/// started with test commands enabled. Supports <c>mode</c> <c>alwaysOn</c>, <c>off</c>,
/// <c>{ times: n }</c> and <c>{ skip: n }</c>, and the <c>data</c> fields <c>failCommands</c>,
/// <c>errorCode</c>, <c>errorLabels</c>, <c>closeConnection</c>, <c>blockConnection</c>/<c>blockTimeMS</c>,
/// <c>writeConcernError</c> and <c>appName</c>.
/// </summary>
internal sealed class FailPoints
{
    private readonly object _lock = new();
    private FailCommandConfig? _failCommand;
    private long _timesEntered;

    public BsonDocument Configure(BsonDocument command)
    {
        var nameValue = command.GetElement(0).Value;
        if (!nameValue.IsString || nameValue.AsString != "failCommand")
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", $"Cannot find failpoint '{nameValue}'; only 'failCommand' is supported");

        if (!command.TryGetValue("mode", out var mode))
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "missing 'mode'");

        var data = command.TryGetValue("data", out var dataValue) && dataValue is BsonDocument d ? d : new BsonDocument();

        lock (_lock)
        {
            long count = _timesEntered;

            if (mode.IsString && mode.AsString == "off")
            {
                _failCommand = null;
            }
            else
            {
                long? times = null;
                long skip = 0;
                if (mode is BsonDocument modeDoc)
                {
                    if (modeDoc.TryGetValue("times", out var t))
                        times = t.ToInt64();
                    else if (modeDoc.TryGetValue("skip", out var s))
                        skip = s.ToInt64();
                    else
                        throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "mode must be 'alwaysOn', 'off', {times: n} or {skip: n}");
                }
                else if (!mode.IsString || mode.AsString != "alwaysOn")
                {
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "mode must be 'alwaysOn', 'off', {times: n} or {skip: n}");
                }

                _failCommand = FailCommandConfig.Parse(data, times, skip);
            }

            _timesEntered = 0;
            return new BsonDocument { { "count", count }, { "ok", 1.0 } };
        }
    }

    /// <summary>Whether the failpoint fires for this command, consuming one of its <c>times</c> if so.</summary>
    public FailCommandAction? Evaluate(string commandName, string? appName)
    {
        lock (_lock)
        {
            var config = _failCommand;
            if (config == null || !config.Applies(commandName, appName))
                return null;

            if (config.Skip > 0)
            {
                config.Skip--;
                return null;
            }

            if (config.Times.HasValue)
            {
                if (config.Times.Value <= 0)
                    return null;
                config.Times--;
                if (config.Times.Value == 0)
                    _failCommand = null;
            }

            _timesEntered++;
            return config.Action;
        }
    }

    private sealed class FailCommandConfig
    {
        private HashSet<string> _commands = null!;
        private string? _appName;

        public long? Times { get; set; }

        public long Skip { get; set; }

        public FailCommandAction Action { get; private set; } = null!;

        public static FailCommandConfig Parse(BsonDocument data, long? times, long skip)
        {
            if (!data.TryGetValue("failCommands", out var commands) || !commands.IsBsonArray)
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "failCommand requires 'data.failCommands'");

            bool block = data.TryGetValue("blockConnection", out var b) && b.ToBoolean();
            int blockMs = data.TryGetValue("blockTimeMS", out var bt) ? bt.ToInt32() : 0;
            if (block && !data.Contains("blockTimeMS"))
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "'blockConnection' requires 'blockTimeMS'");

            IReadOnlyList<string>? labels = data.TryGetValue("errorLabels", out var l) && l.IsBsonArray
                ? l.AsBsonArray.Select(v => v.AsString).ToList()
                : null;

            return new FailCommandConfig
            {
                _commands = new HashSet<string>(commands.AsBsonArray.Select(v => v.AsString), StringComparer.OrdinalIgnoreCase),
                _appName = data.TryGetValue("appName", out var app) && app.IsString ? app.AsString : null,
                Times = times,
                Skip = skip,
                Action = new FailCommandAction(
                    ErrorCode: data.TryGetValue("errorCode", out var code) ? code.ToInt32() : null,
                    ErrorMessage: data.TryGetValue("errmsg", out var msg) && msg.IsString ? msg.AsString : null,
                    ErrorLabels: labels,
                    CloseConnection: data.TryGetValue("closeConnection", out var close) && close.ToBoolean(),
                    BlockTime: block ? TimeSpan.FromMilliseconds(blockMs) : TimeSpan.Zero,
                    WriteConcernError: data.TryGetValue("writeConcernError", out var wce) && wce is BsonDocument wceDoc ? wceDoc : null)
            };
        }

        public bool Applies(string commandName, string? appName) =>
            _commands.Contains(commandName) && (_appName == null || _appName == appName);
    }
}
