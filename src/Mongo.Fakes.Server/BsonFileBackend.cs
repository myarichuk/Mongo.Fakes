using MongoDB.Bson;
using Mongo.Fakes.Server.Aggregation;
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

    public BsonFileBackend(string fixtureRootFolder, bool loadFromMongoDump)
    {
        _databases = loadFromMongoDump
            ? LoadFromMongoDump(fixtureRootFolder)
            : LoadAllFixtures(fixtureRootFolder);
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
        if (!command.TryGetValue("aggregate", out var collValue))
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'aggregate' field.");

        if (collValue.IsInt32 && collValue.AsInt32 == 1)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Cannot run aggregation at database level");

        if (!collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "'aggregate' must be a string or 1");

        string collection = collValue.AsString;

        if (!command.TryGetValue("pipeline", out var pipelineValue) || !pipelineValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'pipeline' field");

        var pipeline = (BsonArray)pipelineValue;
        var data = GetCollection(database, collection);

        var executor = new AggregationPipeline();
        var results = executor.Execute(data, pipeline).ToList();

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

    private BsonDocument HandleInsert(string database, BsonDocument command)
    {
        if (!command.TryGetValue("insert", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'insert' field.");

        string collection = collValue.AsString;

        if (!command.TryGetValue("documents", out var docsValue) || !docsValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'documents' field.");

        var documents = (BsonArray)docsValue;
        bool ordered = command.TryGetValue("ordered", out var ordValue) ? ordValue.ToBoolean() : true;

        lock (_lock)
        {
            if (!_databases.TryGetValue(database, out var db))
            {
                db = new Dictionary<string, List<BsonDocument>>();
                _databases[database] = db;
            }

            if (!db.TryGetValue(collection, out var collDocs))
            {
                collDocs = new List<BsonDocument>();
                db[collection] = collDocs;
            }

            var writeErrors = new List<BsonDocument>();
            int insertedCount = 0;

            foreach (int i in Enumerable.Range(0, documents.Count))
            {
                var doc = (BsonDocument)documents[i];

                if (!doc.Contains("_id"))
                    doc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();

                var idValue = doc["_id"];
                bool isDuplicate = collDocs.Any(d => d["_id"].Equals(idValue));

                if (isDuplicate)
                {
                    writeErrors.Add(new BsonDocument
                    {
                        { "index", i },
                        { "code", ErrorCodes.DuplicateKey },
                        { "errmsg", $"E11000 duplicate key error" }
                    });

                    if (ordered)
                        break;
                }
                else
                {
                    collDocs.Add(doc);
                    insertedCount++;
                }
            }

            var result = new BsonDocument { { "ok", 1.0 }, { "n", insertedCount } };
            if (writeErrors.Count > 0)
                result["writeErrors"] = new BsonArray(writeErrors);

            return result;
        }
    }

    private BsonDocument HandleUpdate(string database, BsonDocument command)
    {
        if (!command.TryGetValue("update", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'update' field.");

        string collection = collValue.AsString;

        if (!command.TryGetValue("updates", out var updatesValue) || !updatesValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'updates' field.");

        var updates = (BsonArray)updatesValue;
        bool ordered = command.TryGetValue("ordered", out var ordValue) ? ordValue.ToBoolean() : true;

        lock (_lock)
        {
            if (!_databases.TryGetValue(database, out var db))
            {
                db = new Dictionary<string, List<BsonDocument>>();
                _databases[database] = db;
            }

            if (!db.TryGetValue(collection, out var collDocs))
            {
                collDocs = new List<BsonDocument>();
                db[collection] = collDocs;
            }

            var filterCompiler = new Mongo.Fakes.Core.FilterCompiler();
            var writeErrors = new List<BsonDocument>();
            int matched = 0;
            int modified = 0;
            var upserted = new List<BsonDocument>();

            foreach (int i in Enumerable.Range(0, updates.Count))
            {
                var updateSpec = (BsonDocument)updates[i];

                if (!updateSpec.TryGetValue("q", out var qValue) || !qValue.IsBsonDocument)
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'q' in update spec.");

                var filter = (BsonDocument)qValue;

                if (!updateSpec.TryGetValue("u", out var uValue) || !uValue.IsBsonDocument)
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'u' in update spec.");

                var replacement = (BsonDocument)uValue;

                if (replacement.ElementCount > 0 && replacement.GetElement(0).Name.StartsWith("$"))
                    throw new MongoCommandException(ErrorCodes.FailedToParse, "FailedToParse", "Update operators not supported in Phase 3.");

                bool multi = updateSpec.TryGetValue("multi", out var mValue) ? mValue.ToBoolean() : false;
                bool upsert = updateSpec.TryGetValue("upsert", out var upValue) ? upValue.ToBoolean() : false;

                try
                {
                    var predicate = filterCompiler.Compile(filter);
                    var matchedDocs = collDocs.Where(predicate).ToList();

                    if (matchedDocs.Count > 0)
                    {
                        int docsToUpdate = multi ? matchedDocs.Count : 1;
                        for (int j = 0; j < docsToUpdate; j++)
                        {
                            var oldDoc = matchedDocs[j];
                            var oldId = oldDoc["_id"];
                            var newDoc = new BsonDocument(replacement);
                            newDoc["_id"] = oldId;

                            int idx = collDocs.IndexOf(oldDoc);
                            if (idx >= 0)
                            {
                                collDocs[idx] = newDoc;
                                modified++;
                            }
                        }
                        matched += docsToUpdate;
                    }
                    else if (upsert)
                    {
                        var newDoc = new BsonDocument(replacement);
                        if (!newDoc.Contains("_id"))
                        {
                            if (filter.Contains("_id"))
                                newDoc["_id"] = filter["_id"];
                            else
                                newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
                        }

                        collDocs.Add(newDoc);
                        upserted.Add(new BsonDocument { { "index", i }, { "_id", newDoc["_id"] } });
                        matched++;
                        modified++;
                    }
                }
                catch (NotSupportedException ex)
                {
                    writeErrors.Add(new BsonDocument
                    {
                        { "index", i },
                        { "code", ErrorCodes.BadValue },
                        { "errmsg", ex.Message }
                    });

                    if (ordered)
                        break;
                }
            }

            var result = new BsonDocument { { "ok", 1.0 }, { "n", matched }, { "nModified", modified } };
            if (upserted.Count > 0)
                result["upserted"] = new BsonArray(upserted);
            if (writeErrors.Count > 0)
                result["writeErrors"] = new BsonArray(writeErrors);

            return result;
        }
    }

    private BsonDocument HandleDelete(string database, BsonDocument command)
    {
        if (!command.TryGetValue("delete", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'delete' field.");

        string collection = collValue.AsString;

        if (!command.TryGetValue("deletes", out var deletesValue) || !deletesValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'deletes' field.");

        var deletes = (BsonArray)deletesValue;
        bool ordered = command.TryGetValue("ordered", out var ordValue) ? ordValue.ToBoolean() : true;

        lock (_lock)
        {
            if (!_databases.TryGetValue(database, out var db) || !db.TryGetValue(collection, out var collDocs))
                return new BsonDocument { { "ok", 1.0 }, { "n", 0 } };

            var filterCompiler = new Mongo.Fakes.Core.FilterCompiler();
            int deletedCount = 0;

            foreach (int i in Enumerable.Range(0, deletes.Count))
            {
                var deleteSpec = (BsonDocument)deletes[i];

                if (!deleteSpec.TryGetValue("q", out var qValue) || !qValue.IsBsonDocument)
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'q' in delete spec.");

                var filter = (BsonDocument)qValue;
                int limit = deleteSpec.TryGetValue("limit", out var lValue) ? lValue.ToInt32() : 0;

                try
                {
                    var predicate = filterCompiler.Compile(filter);
                    var matchedDocs = collDocs.Where(predicate).ToList();

                    if (limit == 1 && matchedDocs.Count > 0)
                    {
                        collDocs.Remove(matchedDocs[0]);
                        deletedCount++;
                    }
                    else if (limit == 0)
                    {
                        foreach (var doc in matchedDocs)
                            collDocs.Remove(doc);
                        deletedCount += matchedDocs.Count;
                    }
                }
                catch (NotSupportedException ex)
                {
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", ex.Message);
                }
            }

            return new BsonDocument { { "ok", 1.0 }, { "n", deletedCount } };
        }
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

    private static Dictionary<string, Dictionary<string, List<BsonDocument>>> LoadFromMongoDump(string rootFolder)
    {
        var databases = new Dictionary<string, Dictionary<string, List<BsonDocument>>>();

        if (!Directory.Exists(rootFolder))
            return databases;

        foreach (var dbDir in Directory.EnumerateDirectories(rootFolder))
        {
            var dbName = Path.GetFileName(dbDir);
            var collections = new Dictionary<string, List<BsonDocument>>();

            foreach (var file in Directory.EnumerateFiles(dbDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var collectionName = Path.GetFileNameWithoutExtension(file);

                if (ext == ".json")
                {
                    collections[collectionName] = LoadJsonFile(file);
                }
                else if (ext == ".bson")
                {
                    collections[collectionName] = LoadBsonFile(file);
                }
            }

            if (collections.Count > 0)
                databases[dbName] = collections;
        }

        return databases;
    }

    private static List<BsonDocument> LoadBsonFile(string path)
    {
        var documents = new List<BsonDocument>();

        using (var stream = File.OpenRead(path))
        {
            while (stream.Position < stream.Length)
            {
                byte[] lengthBytes = new byte[4];
                if (stream.Read(lengthBytes, 0, 4) != 4)
                    break;

                int docLength = BitConverter.ToInt32(lengthBytes, 0);
                if (docLength < 5)
                    break;

                byte[] docBytes = new byte[docLength];
                Array.Copy(lengthBytes, docBytes, 4);

                if (stream.Read(docBytes, 4, docLength - 4) != docLength - 4)
                    break;

                try
                {
                    using (var memStream = new System.IO.MemoryStream(docBytes))
                    using (var reader = new MongoDB.Bson.IO.BsonBinaryReader(memStream))
                    {
                        var doc = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(reader);
                        documents.Add(doc);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        return documents;
    }
}
