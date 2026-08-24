using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server;

public sealed class BsonFileBackend : IMongoBackend
{
    private readonly Dictionary<string, Dictionary<string, List<BsonDocument>>> _databases;
    private readonly object _lock = new();

    public BsonFileBackend(string fixtureRootFolder)
    {
        _databases = LoadAllFixtures(fixtureRootFolder);
    }

    public IReadOnlyList<BsonDocument> GetCollection(string database, string collection)
    {
        lock (_lock)
        {
            if (_databases.TryGetValue(database, out var db) && db.TryGetValue(collection, out var docs))
                return docs.ToList();
            return [];
        }
    }

    public async Task<BsonDocument> ExecuteCommandAsync(string database, BsonDocument command, CancellationToken ct)
    {
        if (command.ElementCount == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Empty command document.");

        string commandName = command.GetElement(0).Name.ToLowerInvariant();

        try
        {
            return commandName switch
            {
                "find" => HandleFind(database, command),
                "count" => HandleCount(database, command),
                "aggregate" => HandleAggregate(database, command),
                "insert" => HandleInsert(database, command),
                "update" => HandleUpdate(database, command),
                "delete" => HandleDelete(database, command),
                "listdatabases" => HandleListDatabases(),
                "listcollections" => HandleListCollections(database),
                _ => throw new MongoCommandException(ErrorCodes.CommandNotFound, "CommandNotFound", $"no such cmd: {commandName}")
            };
        }
        catch (NotSupportedException ex)
        {
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", ex.Message);
        }
    }

    private BsonDocument HandleFind(string database, BsonDocument command)
    {
        if (!command.TryGetValue("find", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'find' field.");

        string collection = collValue.AsString;
        var data = GetCollection(database, collection);

        var filter = command.TryGetValue("filter", out var f) ? (BsonDocument)f : new BsonDocument();
        var sort = command.TryGetValue("sort", out var s) ? (BsonDocument)s : null;
        var projection = command.TryGetValue("projection", out var p) ? (BsonDocument)p : null;
        int skip = command.TryGetValue("skip", out var sk) ? sk.ToInt32() : 0;
        int limit = command.TryGetValue("limit", out var l) ? l.ToInt32() : 0;

        if (limit < 0)
            limit = Math.Abs(limit);

        var executor = new BsonQueryExecutor();
        var results = executor.ExecuteFind(data, filter, projection, sort, skip, limit).ToList();

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "cursor", new BsonDocument
            {
                { "id", 0L },
                { "ns", $"{database}.{collection}" },
                { "firstBatch", new BsonArray(results) }
            }}
        };
    }

    private BsonDocument HandleCount(string database, BsonDocument command)
    {
        if (!command.TryGetValue("count", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'count' field.");

        string collection = collValue.AsString;
        var data = GetCollection(database, collection);

        var query = command.TryGetValue("query", out var q) ? (BsonDocument)q : new BsonDocument();
        int skip = command.TryGetValue("skip", out var sk) ? sk.ToInt32() : 0;
        int limit = command.TryGetValue("limit", out var l) ? l.ToInt32() : 0;

        var executor = new BsonQueryExecutor();
        int count = executor.ExecuteCount(data, query, skip, limit);

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "n", count }
        };
    }

    private BsonDocument HandleAggregate(string database, BsonDocument command)
    {
        throw new NotImplementedException("Aggregation is implemented in Phase 4.");
    }

    private BsonDocument HandleInsert(string database, BsonDocument command)
    {
        throw new NotImplementedException("Insert is implemented in Phase 3.");
    }

    private BsonDocument HandleUpdate(string database, BsonDocument command)
    {
        throw new NotImplementedException("Update is implemented in Phase 3.");
    }

    private BsonDocument HandleDelete(string database, BsonDocument command)
    {
        throw new NotImplementedException("Delete is implemented in Phase 3.");
    }

    private BsonDocument HandleListDatabases()
    {
        lock (_lock)
        {
            var databases = new BsonArray(_databases.Keys.Select(k => new BsonDocument { { "name", k } }));
            return new BsonDocument
            {
                { "ok", 1.0 },
                { "databases", databases }
            };
        }
    }

    private BsonDocument HandleListCollections(string database)
    {
        lock (_lock)
        {
            if (!_databases.TryGetValue(database, out var db))
            {
                return new BsonDocument
                {
                    { "ok", 1.0 },
                    { "cursor", new BsonDocument
                    {
                        { "id", 0L },
                        { "ns", $"{database}.$cmd.listCollections" },
                        { "firstBatch", new BsonArray() }
                    }}
                };
            }

            var collections = new BsonArray(db.Keys.Select(k => new BsonDocument { { "name", k } }));
            return new BsonDocument
            {
                { "ok", 1.0 },
                { "cursor", new BsonDocument
                {
                    { "id", 0L },
                    { "ns", $"{database}.$cmd.listCollections" },
                    { "firstBatch", collections }
                }}
            };
        }
    }

    private static Dictionary<string, Dictionary<string, List<BsonDocument>>> LoadAllFixtures(string rootFolder)
    {
        var databases = new Dictionary<string, Dictionary<string, List<BsonDocument>>>();

        if (!Directory.Exists(rootFolder))
        {
            return databases;
        }

        foreach (var dbDir in Directory.EnumerateDirectories(rootFolder))
        {
            var collections = new Dictionary<string, List<BsonDocument>>();

            foreach (var file in Directory.EnumerateFiles(dbDir, "*.json"))
            {
                var collectionName = Path.GetFileNameWithoutExtension(file);
                collections[collectionName] = LoadJsonFile(file);
            }

            if (collections.Count > 0)
            {
                databases[Path.GetFileName(dbDir)] = collections;
            }
        }

        return databases;
    }

    private static List<BsonDocument> LoadJsonFile(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(BsonDocument.Parse)
            .ToList();
}
