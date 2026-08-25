using MongoDB.Bson;
using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Aggregation;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Update;

namespace Mongo.Fakes.Server;

public sealed class BsonFileBackend : IMongoBackend
{
    private readonly IBaselineDataProvider _baseline;
    private readonly Dictionary<string, DocumentSnapshot> _snapshots;
    private readonly HashSet<string> _deletedIds;
    private readonly Dictionary<(string Database, string Collection), TextIndexSpec> _textIndexes;
    private readonly object _lock = new();

    public BsonFileBackend(IBaselineDataProvider baseline)
    {
        _baseline = baseline;
        _snapshots = new();
        _deletedIds = new();
        _textIndexes = new();
    }

    public BsonFileBackend(string fixtureRootFolder)
        : this(new FileBasedBaselineProvider(fixtureRootFolder))
    {
    }

    public BsonFileBackend(string fixtureRootFolder, bool loadFromMongoDump)
        : this(new FileBasedBaselineProvider(fixtureRootFolder, loadFromMongoDump))
    {
    }

    public IReadOnlyList<BsonDocument> GetCollection(string database, string collection)
    {
        var baselineData = _baseline.GetCollection(database, collection);
        lock (_lock)
        {
            var result = new List<BsonDocument>();
            var processedIds = new HashSet<string>();

            foreach (var doc in baselineData)
            {
                var idKey = GetSnapshotKey(doc["_id"]);
                processedIds.Add(idKey);

                if (_deletedIds.Contains(idKey))
                    continue;

                var snapshot = GetOrCreateSnapshot(idKey, doc);
                result.Add(snapshot.Current);
            }

            // Add any newly inserted documents that aren't in baseline
            foreach (var kvp in _snapshots)
            {
                if (!processedIds.Contains(kvp.Key) && !_deletedIds.Contains(kvp.Key))
                {
                    result.Add(kvp.Value.Current);
                }
            }

            return result;
        }
    }

    private static string GetSnapshotKey(BsonValue idValue)
    {
        return idValue.ToJson();
    }

    private DocumentSnapshot GetOrCreateSnapshot(string idKey, BsonDocument doc)
    {
        if (!_snapshots.TryGetValue(idKey, out var snapshot))
        {
            snapshot = new DocumentSnapshot(doc);
            _snapshots[idKey] = snapshot;
        }
        return snapshot;
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
                "listcollections" => HandleListCollections(database, command),
                "drop" => HandleDrop(database, command),
                "dropdatabase" => HandleDropDatabase(database),
                "findandmodify" => HandleFindAndModify(database, command),
                "distinct" => HandleDistinct(database, command),
                "createindexes" => HandleCreateIndexes(database, command),
                "listindexes" => HandleListIndexes(database, command),
                "create" => HandleNoOp("create"),
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

        TextIndexSpec? textIndex;
        lock (_lock)
        {
            _textIndexes.TryGetValue((database, collection), out textIndex);
        }

        var executor = new BsonQueryExecutor();
        var results = executor.ExecuteFind(data, filter, projection, sort, skip, limit, textIndex).ToList();

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

        TextIndexSpec? textIndex;
        lock (_lock)
        {
            _textIndexes.TryGetValue((database, collection), out textIndex);
        }

        var executor = new BsonQueryExecutor();
        int count = executor.ExecuteCount(data, query, skip, limit, textIndex);

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

        TextIndexSpec? textIndex;
        lock (_lock)
        {
            _textIndexes.TryGetValue((database, collection), out textIndex);
        }

        var executor = new AggregationPipeline(coll => GetCollection(database, coll), null, textIndex);
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
            var writeErrors = new List<BsonDocument>();
            int insertedCount = 0;

            foreach (int i in Enumerable.Range(0, documents.Count))
            {
                var doc = (BsonDocument)documents[i];

                if (!doc.Contains("_id"))
                    doc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();

                var idKey = GetSnapshotKey(doc["_id"]);
                bool isDuplicate = _snapshots.ContainsKey(idKey);

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
                    _snapshots[idKey] = new DocumentSnapshot(doc);
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
            var baselineData = _baseline.GetCollection(database, collection);
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
                bool isOperatorUpdate = replacement.ElementCount > 0 && replacement.GetElement(0).Name.StartsWith("$");

                bool multi = updateSpec.TryGetValue("multi", out var mValue) ? mValue.ToBoolean() : false;
                bool upsert = updateSpec.TryGetValue("upsert", out var upValue) ? upValue.ToBoolean() : false;

                try
                {
                    var predicate = filterCompiler.Compile(filter);
                    var matchedKeys = new List<string>();

                    foreach (var doc in baselineData)
                    {
                        var idKey = GetSnapshotKey(doc["_id"]);
                        var snapshot = GetOrCreateSnapshot(idKey, doc);
                        if (predicate(snapshot.Current))
                            matchedKeys.Add(idKey);
                    }

                    if (matchedKeys.Count > 0)
                    {
                        int docsToUpdate = multi ? matchedKeys.Count : 1;
                        for (int j = 0; j < docsToUpdate; j++)
                        {
                            var idKey = matchedKeys[j];
                            var snapshot = _snapshots[idKey];
                            var oldDoc = snapshot.Current;

                            BsonDocument newDoc;
                            if (isOperatorUpdate)
                            {
                                newDoc = UpdateApplier.ApplyOperators(oldDoc, replacement);
                            }
                            else
                            {
                                newDoc = new BsonDocument(replacement);
                                newDoc["_id"] = oldDoc["_id"];
                            }

                            if (newDoc.ToJson() != oldDoc.ToJson())
                                modified++;

                            snapshot.Mutated = newDoc;
                        }
                        matched += docsToUpdate;
                    }
                    else if (upsert)
                    {
                        BsonDocument newDoc;
                        if (isOperatorUpdate)
                        {
                            newDoc = new BsonDocument();
                            if (!newDoc.Contains("_id"))
                            {
                                if (filter.Contains("_id"))
                                    newDoc["_id"] = filter["_id"];
                                else
                                    newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
                            }
                            newDoc = UpdateApplier.ApplyOperators(newDoc, replacement, isUpsertInsert: true);
                        }
                        else
                        {
                            newDoc = new BsonDocument(replacement);
                            if (!newDoc.Contains("_id"))
                            {
                                if (filter.Contains("_id"))
                                    newDoc["_id"] = filter["_id"];
                                else
                                    newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
                            }
                        }

                        var idKey = GetSnapshotKey(newDoc["_id"]);
                        _snapshots[idKey] = new DocumentSnapshot(newDoc);
                        upserted.Add(new BsonDocument { { "index", i }, { "_id", newDoc["_id"] } });
                        matched++;
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
            var baselineData = _baseline.GetCollection(database, collection);
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
                    var matchedKeys = new List<string>();

                    foreach (var doc in baselineData)
                    {
                        var idKey = GetSnapshotKey(doc["_id"]);
                        if (!_deletedIds.Contains(idKey))
                        {
                            var snapshot = GetOrCreateSnapshot(idKey, doc);
                            if (predicate(snapshot.Current))
                                matchedKeys.Add(idKey);
                        }
                    }

                    // Also check newly inserted documents
                    foreach (var kvp in _snapshots)
                    {
                        if (!baselineData.Any(d => GetSnapshotKey(d["_id"]) == kvp.Key) && !_deletedIds.Contains(kvp.Key))
                        {
                            if (predicate(kvp.Value.Current))
                                matchedKeys.Add(kvp.Key);
                        }
                    }

                    if (limit == 1 && matchedKeys.Count > 0)
                    {
                        _deletedIds.Add(matchedKeys[0]);
                        deletedCount++;
                    }
                    else if (limit == 0)
                    {
                        foreach (var key in matchedKeys)
                            _deletedIds.Add(key);
                        deletedCount += matchedKeys.Count;
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
            var databaseNames = _baseline.GetDatabases();
            var databases = new BsonArray(databaseNames.Select(k => new BsonDocument { { "name", k } }));
            return new BsonDocument
            {
                { "ok", 1.0 },
                { "databases", databases }
            };
        }
    }

    private BsonDocument HandleListCollections(string database, BsonDocument command)
    {
        var filter = command.TryGetValue("filter", out var fValue) ? (BsonDocument)fValue : new BsonDocument();

        lock (_lock)
        {
            var collectionNames = _baseline.GetCollections(database);
            if (collectionNames.Count == 0)
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

            var allCollections = collectionNames.Select(k => new BsonDocument { { "name", k } }).ToList();

            if (filter.ElementCount > 0)
            {
                var filterCompiler = new FilterCompiler();
                var predicate = filterCompiler.Compile(filter);
                allCollections = allCollections.Where(predicate).ToList();
            }

            var collections = new BsonArray(allCollections);
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

    private BsonDocument HandleDrop(string database, BsonDocument command)
    {
        if (!command.TryGetValue("drop", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'drop' field.");

        return new BsonDocument { { "ok", 1.0 } };
    }

    private BsonDocument HandleDropDatabase(string database)
    {
        return new BsonDocument { { "ok", 1.0 } };
    }

    private BsonDocument HandleDistinct(string database, BsonDocument command)
    {
        if (!command.TryGetValue("distinct", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'distinct' field.");

        if (!command.TryGetValue("key", out var keyValue) || !keyValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'key' field.");

        string collection = collValue.AsString;
        string key = keyValue.AsString;

        var data = GetCollection(database, collection);
        var filter = command.TryGetValue("query", out var f) ? (BsonDocument)f : new BsonDocument();

        TextIndexSpec? textIndex;
        lock (_lock)
        {
            _textIndexes.TryGetValue((database, collection), out textIndex);
        }

        var executor = new BsonQueryExecutor();
        var results = executor.ExecuteFind(data, filter, null, null, 0, 0, textIndex).ToList();

        var distinctValues = new HashSet<string>();
        foreach (var doc in results)
        {
            var value = BsonPath.GetValue(doc, key);
            if (value != null)
                distinctValues.Add(value.ToJson());
        }

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "values", new BsonArray(distinctValues.Select(v => BsonValue.Create(v))) }
        };
    }

    private BsonDocument HandleListIndexes(string database, BsonDocument command)
    {
        if (!command.TryGetValue("listIndexes", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'listIndexes' field.");

        string collection = collValue.AsString;
        var indexes = new BsonArray();

        // Always include the default _id index
        indexes.Add(new BsonDocument
        {
            { "v", 2 },
            { "key", new BsonDocument { { "_id", 1 } } },
            { "name", "_id_" }
        });

        // Include text index if one exists for this collection
        lock (_lock)
        {
            if (_textIndexes.TryGetValue((database, collection), out var textIndex))
            {
                var keyDoc = new BsonDocument();
                if (textIndex.IsWildcard)
                {
                    keyDoc["$**"] = "text";
                }
                else
                {
                    foreach (var field in textIndex.Fields)
                    {
                        keyDoc[field] = "text";
                    }
                }

                indexes.Add(new BsonDocument
                {
                    { "v", 2 },
                    { "key", keyDoc },
                    { "name", textIndex.Fields.Count > 0 ? $"{string.Join("_", textIndex.Fields)}_text" : "$**_text" },
                    { "default_language", "english" },
                    { "textIndexVersion", 3 }
                });
            }
        }

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "cursor", new BsonDocument
            {
                { "id", 0L },
                { "ns", $"{database}.{collection}" },
                { "firstBatch", indexes }
            }}
        };
    }

    private BsonDocument HandleCreateIndexes(string database, BsonDocument command)
    {
        if (!command.TryGetValue("createIndexes", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'createIndexes' field.");

        if (!command.TryGetValue("indexes", out var indexesValue) || !indexesValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'indexes' field.");

        string collection = collValue.AsString;
        var indexesArray = (BsonArray)indexesValue;

        lock (_lock)
        {
            int numIndexesBefore = _textIndexes.ContainsKey((database, collection)) ? 1 : 0;
            int numIndexesAfter = numIndexesBefore;

            foreach (var indexElem in indexesArray)
            {
                if (indexElem is not BsonDocument indexDoc)
                    continue;

                if (!indexDoc.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument keyDoc)
                    continue;

                var textIndex = TextIndexSpec.TryCreate(keyDoc);
                if (textIndex != null)
                {
                    var key = (database, collection);
                    if (_textIndexes.TryGetValue(key, out var existing))
                    {
                        // Check if it's identical (idempotent)
                        if (existing.IsWildcard == textIndex.IsWildcard &&
                            existing.Fields.SequenceEqual(textIndex.Fields))
                        {
                            continue; // Already exists, no error
                        }

                        // Genuine conflict: two different text indexes
                        throw new MongoCommandException(
                            ErrorCodes.BadValue,
                            "BadValue",
                            "Only one text index is allowed per collection.");
                    }

                    _textIndexes[key] = textIndex;
                    numIndexesAfter++;
                }
            }

            return new BsonDocument
            {
                { "ok", 1.0 },
                { "numIndexesBefore", numIndexesBefore },
                { "numIndexesAfter", numIndexesAfter }
            };
        }
    }

    private BsonDocument HandleFindAndModify(string database, BsonDocument command)
    {
        if (!command.TryGetValue("findandmodify", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'findandmodify' field.");

        string collection = collValue.AsString;
        var filter = command.TryGetValue("query", out var qValue) ? (BsonDocument)qValue : new BsonDocument();
        var sort = command.TryGetValue("sort", out var sValue) ? (BsonDocument)sValue : null;
        bool returnNew = command.TryGetValue("new", out var newValue) ? newValue.ToBoolean() : false;
        bool upsert = command.TryGetValue("upsert", out var upValue) ? upValue.ToBoolean() : false;

        bool isUpdate = command.Contains("update");
        bool isRemove = command.TryGetValue("remove", out var removeValue) && removeValue.ToBoolean();

        if (isRemove && (isUpdate || upsert))
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Cannot specify both remove and update/upsert in findAndModify");

        var data = GetCollection(database, collection);

        TextIndexSpec? textIndex;
        lock (_lock)
        {
            _textIndexes.TryGetValue((database, collection), out textIndex);
        }

        var executor = new BsonQueryExecutor();
        var results = executor.ExecuteFind(data, filter, null, sort, 0, 1, textIndex).ToList();

        if (results.Count == 0)
        {
            if (isUpdate && upsert && command.TryGetValue("update", out var updateValue))
            {
                var update = (BsonDocument)updateValue;
                bool isOperatorUpdate = update.ElementCount > 0 && update.GetElement(0).Name.StartsWith("$");

                lock (_lock)
                {
                    BsonDocument newDoc;
                    if (isOperatorUpdate)
                    {
                        newDoc = new BsonDocument();
                        if (!newDoc.Contains("_id"))
                        {
                            if (filter.Contains("_id"))
                                newDoc["_id"] = filter["_id"];
                            else
                                newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
                        }
                        newDoc = UpdateApplier.ApplyOperators(newDoc, update, isUpsertInsert: true);
                    }
                    else
                    {
                        newDoc = new BsonDocument(update);
                        if (!newDoc.Contains("_id"))
                        {
                            if (filter.Contains("_id"))
                                newDoc["_id"] = filter["_id"];
                            else
                                newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
                        }
                    }

                    var idKey = GetSnapshotKey(newDoc["_id"]);
                    _snapshots[idKey] = new DocumentSnapshot(newDoc);
                    return new BsonDocument
                    {
                        { "ok", 1.0 },
                        { "value", returnNew ? newDoc : BsonNull.Value }
                    };
                }
            }

            return new BsonDocument { { "ok", 1.0 }, { "value", BsonNull.Value } };
        }

        var foundDoc = results[0];
        var returnValue = new BsonDocument(foundDoc);
        BsonDocument? updatedDoc = null;
        var foundDocKey = GetSnapshotKey(foundDoc["_id"]);

        if (isUpdate && command.TryGetValue("update", out var updateValue2))
        {
            var update = (BsonDocument)updateValue2;
            bool isOperatorUpdate = update.ElementCount > 0 && update.GetElement(0).Name.StartsWith("$");

            lock (_lock)
            {
                var snapshot = GetOrCreateSnapshot(foundDocKey, foundDoc);
                BsonDocument newDoc;
                if (isOperatorUpdate)
                {
                    newDoc = UpdateApplier.ApplyOperators(foundDoc, update);
                }
                else
                {
                    newDoc = new BsonDocument(update);
                    newDoc["_id"] = foundDoc["_id"];
                }

                updatedDoc = newDoc;
                snapshot.Mutated = newDoc;
            }
        }
        else if (isRemove)
        {
            lock (_lock)
            {
                _deletedIds.Add(foundDocKey);
            }
        }

        BsonValue valueToReturn;
        if (isUpdate && returnNew && updatedDoc != null)
            valueToReturn = updatedDoc;
        else if (isRemove)
            valueToReturn = returnValue;
        else
            valueToReturn = returnValue;

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "value", valueToReturn }
        };
    }

    private static BsonDocument HandleNoOp(string commandName)
    {
        return new BsonDocument { { "ok", 1.0 } };
    }
}
