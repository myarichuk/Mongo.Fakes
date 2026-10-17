using MongoDB.Bson;
using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Aggregation;
using Mongo.Fakes.Server.Cursors;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Update;

namespace Mongo.Fakes.Server;

public sealed class BsonFileBackend : IMongoBackend
{
    private readonly IBaselineDataProvider _baseline;
    private readonly Dictionary<(string Database, string Collection), CollectionStore> _stores;
    private readonly Dictionary<(string Database, string Collection), TextIndexSpec> _textIndexes;
    private readonly Dictionary<(string Database, string Collection), Dictionary<string, IndexSpec>> _indexes;
    private readonly object _lock = new();

    /// <summary>Every write, for change streams.</summary>
    internal ChangeEventHub ChangeEvents { get; } = new();

    public BsonFileBackend(IBaselineDataProvider baseline)
    {
        _baseline = baseline;
        _stores = new();
        _textIndexes = new();
        _indexes = new();
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
        lock (_lock)
        {
            return GetOrCreateStore((database, collection)).CurrentDocuments();
        }
    }

    private CollectionStore GetOrCreateStore((string Database, string Collection) collKey)
    {
        if (!_stores.TryGetValue(collKey, out var store))
        {
            store = new CollectionStore(collKey.Database, collKey.Collection,
                _baseline.GetCollection(collKey.Database, collKey.Collection),
                existsInBaseline: _baseline.GetCollections(collKey.Database).Contains(collKey.Collection));
            _stores[collKey] = store;
        }
        return store;
    }

    /// <summary>
    /// Yields <paramref name="data"/>, checking <paramref name="ct"/> per document so that a
    /// <c>maxTimeMS</c> deadline interrupts a long scan.
    /// </summary>
    private static IEnumerable<BsonDocument> Cancellable(IReadOnlyList<BsonDocument> data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ct.CanBeCanceled)
            return data;
        return Iterate();

        IEnumerable<BsonDocument> Iterate()
        {
            foreach (var doc in data)
            {
                ct.ThrowIfCancellationRequested();
                yield return doc;
            }
        }
    }

    private Dictionary<string, IndexSpec> GetOrCreateIndexes((string Database, string Collection) collKey)
    {
        if (!_indexes.TryGetValue(collKey, out var indexes))
        {
            indexes = new();
            _indexes[collKey] = indexes;
        }
        return indexes;
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
                "find" => HandleFind(database, command, ct),
                "count" => HandleCount(database, command, ct),
                "aggregate" => HandleAggregate(database, command, ct),
                "insert" => HandleInsert(database, command),
                "update" => HandleUpdate(database, command, ct),
                "delete" => HandleDelete(database, command, ct),
                "listdatabases" => HandleListDatabases(command),
                "listcollections" => HandleListCollections(database, command),
                "drop" => HandleDrop(database, command),
                "dropdatabase" => HandleDropDatabase(database),
                "renamecollection" => HandleRenameCollection(database, command),
                "collmod" => HandleCollMod(database, command),
                "dbstats" => HandleDbStats(database, command),
                "collstats" => HandleCollStats(database, command),
                "explain" => HandleExplain(database, command, ct),
                "findandmodify" => HandleFindAndModify(database, command, ct),
                "distinct" => HandleDistinct(database, command, ct),
                "createindexes" => HandleCreateIndexes(database, command),
                "listindexes" => HandleListIndexes(database, command),
                "dropindexes" => HandleDropIndexes(database, command),
                "create" => HandleCreate(database, command),
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

    private string GetCommandCollectionName(BsonDocument command, string commandKey)
    {
        // Try case-insensitive lookup for the command key
        foreach (var element in command)
        {
            if (element.Name.Equals(commandKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!element.Value.IsString)
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", $"Missing '{commandKey}' field.");
                return element.Value.AsString;
            }
        }
        throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", $"Missing '{commandKey}' field.");
    }

    private BsonValue GetCommandValue(BsonDocument command, string commandKey)
    {
        // Try case-insensitive lookup for the command key
        foreach (var element in command)
        {
            if (element.Name.Equals(commandKey, StringComparison.OrdinalIgnoreCase))
                return element.Value;
        }
        throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", $"Missing '{commandKey}' field.");
    }

    private BsonDocument HandleFind(string database, BsonDocument command, CancellationToken ct)
    {
        string collection = GetCommandCollectionName(command, "find");
        var data = Cancellable(GetCollection(database, collection), ct);

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

    private BsonDocument HandleCount(string database, BsonDocument command, CancellationToken ct)
    {
        string collection = GetCommandCollectionName(command, "count");
        var data = Cancellable(GetCollection(database, collection), ct);

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

    private BsonDocument HandleAggregate(string database, BsonDocument command, CancellationToken ct)
    {
        var collValue = GetCommandValue(command, "aggregate");

        if (collValue.IsNumeric && collValue.ToInt32() == 1)
            return HandleDatabaseAggregate(database, command, ct);

        if (!collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "'aggregate' must be a string or 1");

        string collection = collValue.AsString;

        if (!command.TryGetValue("pipeline", out var pipelineValue) || !pipelineValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'pipeline' field");

        var pipeline = (BsonArray)pipelineValue;
        var data = Cancellable(GetCollection(database, collection), ct);

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

    /// <summary>
    /// <c>{ aggregate: 1 }</c>: a pipeline with no collection, which must start with <c>$documents</c>.
    /// (<c>$currentOp</c> and <c>$changeStream</c> are served by the router.)
    /// </summary>
    private BsonDocument HandleDatabaseAggregate(string database, BsonDocument command, CancellationToken ct)
    {
        if (!command.TryGetValue("pipeline", out var pipelineValue) || !pipelineValue.IsBsonArray || pipelineValue.AsBsonArray.Count == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'pipeline' field");

        var pipeline = pipelineValue.AsBsonArray;
        var first = pipeline[0] as BsonDocument;
        string firstStage = first?.ElementCount > 0 ? first.GetElement(0).Name : "";
        if (firstStage != "$documents")
            throw new MongoCommandException(ErrorCodes.InvalidNamespace, "InvalidNamespace",
                $"{{aggregate: 1}} is not valid for '{firstStage}'; a collection is required.");

        if (first!["$documents"] is not BsonArray documents || documents.Any(d => !d.IsBsonDocument))
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$documents must be an array of documents");

        var data = Cancellable(documents.Select(d => d.AsBsonDocument).ToList(), ct);
        var executor = new AggregationPipeline(coll => GetCollection(database, coll));
        var results = executor.Execute(data, new BsonArray(pipeline.Skip(1))).ToList();

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "cursor", new BsonDocument
            {
                { "id", 0L },
                { "ns", $"{database}.$cmd.aggregate" },
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
            var store = GetOrCreateStore((database, collection));
            var writeErrors = new List<BsonDocument>();
            int insertedCount = 0;

            for (int i = 0; i < documents.Count; i++)
            {
                var doc = (BsonDocument)documents[i];
                var error = store.Insert(doc);
                if (error != null)
                {
                    writeErrors.Add(error.ToWriteError(i));
                    if (ordered)
                        break;
                    continue;
                }

                ChangeEvents.Publish("insert", database, collection, doc["_id"], fullDocument: doc);
                insertedCount++;
            }

            var result = new BsonDocument { { "ok", 1.0 }, { "n", insertedCount } };
            if (writeErrors.Count > 0)
                result["writeErrors"] = new BsonArray(writeErrors);

            return result;
        }
    }

    private BsonDocument HandleUpdate(string database, BsonDocument command, CancellationToken ct)
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
            var store = GetOrCreateStore((database, collection));
            var filterCompiler = new Mongo.Fakes.Core.FilterCompiler();
            var writeErrors = new List<BsonDocument>();
            int matched = 0;
            int modified = 0;
            var upserted = new List<BsonDocument>();

            for (int i = 0; i < updates.Count; i++)
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

                StoreError? error = null;
                try
                {
                    var predicate = filterCompiler.Compile(filter);
                    var matchedSnapshots = new List<DocumentSnapshot>();

                    foreach (var snapshot in store.Snapshots)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (predicate(snapshot.Current))
                        {
                            matchedSnapshots.Add(snapshot);
                            if (!multi)
                                break;
                        }
                    }

                    if (matchedSnapshots.Count > 0)
                    {
                        foreach (var snapshot in matchedSnapshots)
                        {
                            var oldDoc = snapshot.Current;

                            BsonDocument newDoc;
                            if (isOperatorUpdate)
                            {
                                newDoc = UpdateApplier.ApplyOperators(oldDoc, replacement);
                            }
                            else
                            {
                                // The store rejects a replacement that changes _id.
                                newDoc = new BsonDocument(replacement);
                                if (!newDoc.Contains("_id"))
                                    newDoc.InsertAt(0, new BsonElement("_id", oldDoc["_id"]));
                            }

                            bool changed = !newDoc.Equals(oldDoc);
                            if (changed)
                            {
                                error = store.Replace(snapshot, newDoc);
                                if (error != null)
                                    break;
                                PublishUpdate(database, collection, oldDoc, newDoc, isOperatorUpdate);
                                modified++;
                            }

                            matched++;
                        }
                    }
                    else if (upsert)
                    {
                        BsonDocument newDoc;
                        if (isOperatorUpdate)
                        {
                            newDoc = new BsonDocument();
                            if (filter.Contains("_id"))
                                newDoc["_id"] = filter["_id"];
                            else
                                newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
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

                        error = store.Insert(newDoc);
                        if (error == null)
                        {
                            ChangeEvents.Publish("insert", database, collection, newDoc["_id"], fullDocument: newDoc);
                            upserted.Add(new BsonDocument { { "index", i }, { "_id", newDoc["_id"] } });
                            matched++;
                        }
                    }
                }
                catch (NotSupportedException ex)
                {
                    error = new StoreError(ErrorCodes.BadValue, "BadValue", ex.Message);
                }
                catch (MongoCommandException ex) when (ex.Code == ErrorCodes.ImmutableField)
                {
                    error = new StoreError(ex.Code, ex.CodeName, ex.Message);
                }

                if (error != null)
                {
                    writeErrors.Add(error.ToWriteError(i));
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

    private BsonDocument HandleDelete(string database, BsonDocument command, CancellationToken ct)
    {
        if (!command.TryGetValue("delete", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'delete' field.");

        string collection = collValue.AsString;

        if (!command.TryGetValue("deletes", out var deletesValue) || !deletesValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'deletes' field.");

        var deletes = (BsonArray)deletesValue;

        lock (_lock)
        {
            var store = GetOrCreateStore((database, collection));
            var filterCompiler = new Mongo.Fakes.Core.FilterCompiler();
            int deletedCount = 0;

            for (int i = 0; i < deletes.Count; i++)
            {
                var deleteSpec = (BsonDocument)deletes[i];

                if (!deleteSpec.TryGetValue("q", out var qValue) || !qValue.IsBsonDocument)
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'q' in delete spec.");

                var filter = (BsonDocument)qValue;
                int limit = deleteSpec.TryGetValue("limit", out var lValue) ? lValue.ToInt32() : 0;

                try
                {
                    var predicate = filterCompiler.Compile(filter);
                    var matched = new List<BsonDocument>();

                    foreach (var snapshot in store.Snapshots)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (predicate(snapshot.Current))
                        {
                            matched.Add(snapshot.Current);
                            if (limit == 1)
                                break;
                        }
                    }

                    foreach (var doc in matched)
                    {
                        if (store.Remove(doc["_id"]))
                        {
                            ChangeEvents.Publish("delete", database, collection, doc["_id"], fullDocumentBeforeChange: doc);
                            deletedCount++;
                        }
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

    private bool CollectionExists((string Database, string Collection) collKey) =>
        _stores.TryGetValue(collKey, out var store)
            ? store.Exists
            : _baseline.GetCollections(collKey.Database).Contains(collKey.Collection);

    /// <summary>The collections of a database that exist now: from the fixtures or created since, minus dropped ones.</summary>
    private List<string> CollectionNames(string database)
    {
        var names = new HashSet<string>(_baseline.GetCollections(database));
        foreach (var key in _stores.Keys)
        {
            if (key.Database == database)
                names.Add(key.Collection);
        }
        return names.Where(n => CollectionExists((database, n))).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private List<string> DatabaseNames()
    {
        var names = new HashSet<string>(_baseline.GetDatabases());
        foreach (var key in _stores.Keys)
            names.Add(key.Database);
        return names.Where(db => CollectionNames(db).Count > 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private BsonDocument HandleListDatabases(BsonDocument command)
    {
        bool nameOnly = command.TryGetValue("nameOnly", out var n) && n.ToBoolean();

        lock (_lock)
        {
            var databases = new BsonArray();
            long totalSize = 0;
            foreach (var name in DatabaseNames())
            {
                if (nameOnly)
                {
                    databases.Add(new BsonDocument("name", name));
                    continue;
                }

                long size = CollectionNames(name).Sum(c => GetOrCreateStore((name, c)).DataSize());
                totalSize += size;
                databases.Add(new BsonDocument { { "name", name }, { "sizeOnDisk", size }, { "empty", size == 0 } });
            }

            if (command.TryGetValue("filter", out var f) && f is BsonDocument filter && filter.ElementCount > 0)
            {
                var predicate = new FilterCompiler().Compile(filter);
                databases = new BsonArray(databases.Cast<BsonDocument>().Where(predicate));
            }

            var reply = new BsonDocument { { "databases", databases } };
            if (!nameOnly)
            {
                reply["totalSize"] = totalSize;
                reply["totalSizeMb"] = totalSize / (1024 * 1024);
            }
            reply["ok"] = 1.0;
            return reply;
        }
    }

    private BsonDocument HandleListCollections(string database, BsonDocument command)
    {
        var filter = command.TryGetValue("filter", out var fValue) ? (BsonDocument)fValue : new BsonDocument();
        bool nameOnly = command.TryGetValue("nameOnly", out var n) && n.ToBoolean();

        lock (_lock)
        {
            var collections = CollectionNames(database).Select(name => nameOnly
                ? new BsonDocument { { "name", name }, { "type", "collection" } }
                : new BsonDocument
                {
                    { "name", name },
                    { "type", "collection" },
                    { "options", new BsonDocument() },
                    { "info", new BsonDocument("readOnly", false) },
                    { "idIndex", new BsonDocument { { "v", 2 }, { "key", new BsonDocument("_id", 1) }, { "name", "_id_" } } }
                }).ToList();

            if (filter.ElementCount > 0)
            {
                var predicate = new FilterCompiler().Compile(filter);
                collections = collections.Where(predicate).ToList();
            }

            return new BsonDocument
            {
                { "ok", 1.0 },
                { "cursor", new BsonDocument
                {
                    { "id", 0L },
                    { "ns", $"{database}.$cmd.listCollections" },
                    { "firstBatch", new BsonArray(collections) }
                }}
            };
        }
    }

    private BsonDocument HandleCreate(string database, BsonDocument command)
    {
        string collection = GetCommandCollectionName(command, "create");
        lock (_lock)
        {
            var collKey = (database, collection);
            if (CollectionExists(collKey))
                throw new MongoCommandException(ErrorCodes.NamespaceExists, "NamespaceExists", $"Collection {database}.{collection} already exists.");

            GetOrCreateStore(collKey).Exists = true;
            ChangeEvents.Publish("create", database, collection);
            return new BsonDocument { { "ok", 1.0 } };
        }
    }

    private BsonDocument HandleDrop(string database, BsonDocument command)
    {
        if (!command.TryGetValue("drop", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'drop' field.");

        string collection = collValue.AsString;
        lock (_lock)
        {
            var collKey = (database, collection);
            if (!CollectionExists(collKey))
                throw new MongoCommandException(ErrorCodes.NamespaceNotFound, "NamespaceNotFound", "ns not found");

            int nIndexesWas = DropCollection(collKey);
            return new BsonDocument { { "nIndexesWas", nIndexesWas }, { "ns", $"{database}.{collection}" }, { "ok", 1.0 } };
        }
    }

    /// <summary>
    /// Replaces the collection with an empty, non-existent one, so the fixture documents don't come
    /// back on the next read. Returns the number of indexes it had, counting <c>_id_</c>.
    /// </summary>
    private int DropCollection((string Database, string Collection) collKey)
    {
        int nIndexesWas = 1 + (_indexes.TryGetValue(collKey, out var indexes) ? indexes.Count : 0);
        _stores[collKey] = CollectionStore.Dropped(collKey.Database, collKey.Collection);
        _indexes.Remove(collKey);
        _textIndexes.Remove(collKey);
        ChangeEvents.Publish("drop", collKey.Database, collKey.Collection);
        return nIndexesWas;
    }

    private BsonDocument HandleDropDatabase(string database)
    {
        lock (_lock)
        {
            foreach (var collection in CollectionNames(database))
                DropCollection((database, collection));

            ChangeEvents.Publish("dropDatabase", database, null);
            return new BsonDocument { { "dropped", database }, { "ok", 1.0 } };
        }
    }

    private BsonDocument HandleRenameCollection(string database, BsonDocument command)
    {
        if (database != "admin")
            throw new MongoCommandException(ErrorCodes.Unauthorized, "Unauthorized", "renameCollection may only be run against the admin database.");

        var source = SplitNamespace(GetCommandValue(command, "renameCollection"));
        var target = SplitNamespace(command.TryGetValue("to", out var to) ? to : BsonNull.Value);
        bool dropTarget = command.TryGetValue("dropTarget", out var dt) && dt.ToBoolean();

        lock (_lock)
        {
            if (!CollectionExists(source))
                throw new MongoCommandException(ErrorCodes.NamespaceNotFound, "NamespaceNotFound", "source namespace does not exist");
            if (source == target)
                throw new MongoCommandException(ErrorCodes.IllegalOperation, "IllegalOperation", "Can't rename a collection to itself");
            if (CollectionExists(target))
            {
                if (!dropTarget)
                    throw new MongoCommandException(ErrorCodes.NamespaceExists, "NamespaceExists", "target namespace exists");
                DropCollection(target);
            }

            var store = GetOrCreateStore(source);
            store.Rename(target.Database, target.Collection);
            _stores[target] = store;
            _stores[source] = CollectionStore.Dropped(source.Database, source.Collection);

            if (_indexes.Remove(source, out var indexes))
                _indexes[target] = indexes;
            if (_textIndexes.Remove(source, out var textIndex))
                _textIndexes[target] = textIndex;

            ChangeEvents.Publish("rename", source.Database, source.Collection,
                to: new BsonDocument { { "db", target.Database }, { "coll", target.Collection } });
            return new BsonDocument { { "ok", 1.0 } };
        }
    }

    private static (string Database, string Collection) SplitNamespace(BsonValue value)
    {
        if (!value.IsString || value.AsString.IndexOf('.') <= 0)
            throw new MongoCommandException(ErrorCodes.InvalidNamespace, "InvalidNamespace", $"Invalid namespace specified '{value}'");
        string ns = value.AsString;
        int dot = ns.IndexOf('.');
        return (ns[..dot], ns[(dot + 1)..]);
    }

    private BsonDocument HandleCollMod(string database, BsonDocument command)
    {
        string collection = GetCommandCollectionName(command, "collMod");
        var collKey = (database, collection);

        lock (_lock)
        {
            if (!CollectionExists(collKey))
                throw new MongoCommandException(ErrorCodes.NamespaceNotFound, "NamespaceNotFound", "ns does not exist");

            var reply = new BsonDocument();
            if (command.TryGetValue("index", out var indexValue) && indexValue is BsonDocument indexOptions)
            {
                var indexes = GetOrCreateIndexes(collKey);
                IndexSpec? spec = null;
                if (indexOptions.TryGetValue("name", out var name) && name.IsString)
                    indexes.TryGetValue(name.AsString, out spec);
                else if (indexOptions.TryGetValue("keyPattern", out var keyPattern) && keyPattern is BsonDocument keyDoc)
                    spec = indexes.Values.FirstOrDefault(i => i.KeyDocument.Equals(keyDoc));

                if (spec == null)
                    throw new MongoCommandException(ErrorCodes.IndexNotFound, "IndexNotFound", "cannot find index for collMod");

                if (indexOptions.TryGetValue("expireAfterSeconds", out var expire))
                {
                    if (spec.ExpireAfterSeconds.HasValue)
                        reply["expireAfterSeconds_old"] = spec.ExpireAfterSeconds.Value;
                    reply["expireAfterSeconds_new"] = expire.ToInt32();
                    indexes[spec.Name] = spec with { ExpireAfterSeconds = expire.ToInt32() };
                }
            }

            reply["ok"] = 1.0;
            return reply;
        }
    }

    private BsonDocument HandleDbStats(string database, BsonDocument command)
    {
        double scale = command.TryGetValue("scale", out var s) && s.IsNumeric && s.ToDouble() > 0 ? s.ToDouble() : 1;

        lock (_lock)
        {
            var names = CollectionNames(database);
            long objects = 0;
            long dataSize = 0;
            int indexes = 0;
            foreach (var name in names)
            {
                var store = GetOrCreateStore((database, name));
                objects += store.Count;
                dataSize += store.DataSize();
                indexes += 1 + (_indexes.TryGetValue((database, name), out var idx) ? idx.Count : 0);
            }

            return new BsonDocument
            {
                { "db", database },
                { "collections", names.Count },
                { "views", 0 },
                { "objects", objects },
                { "avgObjSize", objects == 0 ? 0.0 : (double)dataSize / objects },
                { "dataSize", dataSize / scale },
                { "storageSize", dataSize / scale },
                { "indexes", indexes },
                { "indexSize", 0.0 },
                { "totalSize", dataSize / scale },
                { "scaleFactor", scale },
                { "fsUsedSize", 0.0 },
                { "fsTotalSize", 0.0 },
                { "ok", 1.0 }
            };
        }
    }

    private BsonDocument HandleCollStats(string database, BsonDocument command)
    {
        string collection = GetCommandCollectionName(command, "collStats");
        double scale = command.TryGetValue("scale", out var s) && s.IsNumeric && s.ToDouble() > 0 ? s.ToDouble() : 1;
        var collKey = (database, collection);

        lock (_lock)
        {
            if (!CollectionExists(collKey))
                throw new MongoCommandException(ErrorCodes.NamespaceNotFound, "NamespaceNotFound", $"Collection [{database}.{collection}] not found.");

            var store = GetOrCreateStore(collKey);
            long size = store.DataSize();
            var indexSizes = new BsonDocument("_id_", 0);
            if (_indexes.TryGetValue(collKey, out var indexes))
            {
                foreach (var name in indexes.Keys)
                    indexSizes[name] = 0;
            }

            return new BsonDocument
            {
                { "ns", $"{database}.{collection}" },
                { "size", size / scale },
                { "count", store.Count },
                { "avgObjSize", store.Count == 0 ? 0.0 : (double)size / store.Count },
                { "storageSize", size / scale },
                { "capped", false },
                { "nindexes", indexSizes.ElementCount },
                { "indexSizes", indexSizes },
                { "totalIndexSize", 0 },
                { "totalSize", size / scale },
                { "scaleFactor", scale },
                { "ok", 1.0 }
            };
        }
    }

    /// <summary>
    /// A collection-scan plan for the explained command. With an execution verbosity, read
    /// commands are run to fill in <c>executionStats</c>; writes are never run.
    /// </summary>
    private BsonDocument HandleExplain(string database, BsonDocument command, CancellationToken ct)
    {
        if (command.GetElement(0).Value is not BsonDocument inner || inner.ElementCount == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "explain command requires a nested object");

        string verbosity = command.TryGetValue("verbosity", out var v) && v.IsString ? v.AsString : "allPlansExecution";
        string innerName = inner.GetElement(0).Name.ToLowerInvariant();
        string collection = inner.GetElement(0).Value.IsString ? inner.GetElement(0).Value.AsString : "$cmd.aggregate";

        BsonDocument filter = innerName switch
        {
            "find" => inner.TryGetValue("filter", out var f) && f is BsonDocument fd ? fd : new BsonDocument(),
            "count" or "distinct" => inner.TryGetValue("query", out var q) && q is BsonDocument qd ? qd : new BsonDocument(),
            "findandmodify" => inner.TryGetValue("query", out var fq) && fq is BsonDocument fqd ? fqd : new BsonDocument(),
            _ => new BsonDocument()
        };

        var plan = new BsonDocument { { "stage", "COLLSCAN" }, { "filter", filter }, { "direction", "forward" } };
        var reply = new BsonDocument
        {
            { "explainVersion", "1" },
            { "queryPlanner", new BsonDocument
            {
                { "namespace", $"{database}.{collection}" },
                { "indexFilterSet", false },
                { "parsedQuery", filter },
                { "winningPlan", plan },
                { "rejectedPlans", new BsonArray() }
            }}
        };

        if (verbosity != "queryPlanner")
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long? nReturned = innerName switch
            {
                "find" => HandleFind(database, inner, ct)["cursor"]["firstBatch"].AsBsonArray.Count,
                "aggregate" => HandleAggregate(database, inner, ct)["cursor"]["firstBatch"].AsBsonArray.Count,
                "count" => HandleCount(database, inner, ct)["n"].ToInt64(),
                "distinct" => HandleDistinct(database, inner, ct)["values"].AsBsonArray.Count,
                _ => null
            };
            stopwatch.Stop();

            if (nReturned.HasValue)
            {
                int docsExamined = inner.GetElement(0).Value.IsString ? GetCollection(database, collection).Count : 0;
                reply["executionStats"] = new BsonDocument
                {
                    { "executionSuccess", true },
                    { "nReturned", nReturned.Value },
                    { "executionTimeMillis", (long)stopwatch.Elapsed.TotalMilliseconds },
                    { "totalKeysExamined", 0 },
                    { "totalDocsExamined", docsExamined },
                    { "executionStages", new BsonDocument
                    {
                        { "stage", "COLLSCAN" },
                        { "nReturned", nReturned.Value },
                        { "docsExamined", docsExamined }
                    }}
                };
            }
        }

        reply["command"] = inner;
        reply["serverInfo"] = new BsonDocument { { "host", "fake" }, { "port", 0 }, { "version", "6.0.0" }, { "gitVersion", "fake" } };
        reply["ok"] = 1.0;
        return reply;
    }

    private BsonDocument HandleDistinct(string database, BsonDocument command, CancellationToken ct)
    {
        if (!command.TryGetValue("distinct", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'distinct' field.");

        if (!command.TryGetValue("key", out var keyValue) || !keyValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'key' field.");

        string collection = collValue.AsString;
        string key = keyValue.AsString;

        var data = Cancellable(GetCollection(database, collection), ct);
        var filter = command.TryGetValue("query", out var f) ? (BsonDocument)f : new BsonDocument();

        TextIndexSpec? textIndex;
        lock (_lock)
        {
            _textIndexes.TryGetValue((database, collection), out textIndex);
        }

        var executor = new BsonQueryExecutor();
        var results = executor.ExecuteFind(data, filter, null, null, 0, 0, textIndex);

        // Like the server, distinct unwinds array values and keeps each value's own type.
        var seen = new HashSet<BsonValue>(Query.BsonKeyComparer.Instance);
        var distinctValues = new BsonArray();
        foreach (var doc in results)
        {
            var value = BsonPath.GetValue(doc, key);
            if (value == null)
                continue;

            IEnumerable<BsonValue> values = value is BsonArray array ? array : [value];
            foreach (var item in values)
            {
                if (seen.Add(item))
                    distinctValues.Add(item);
            }
        }

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "values", distinctValues }
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

        // Include all stored indexes for this collection
        lock (_lock)
        {
            if (_indexes.TryGetValue((database, collection), out var collIndexes))
            {
                foreach (var indexSpec in collIndexes.Values)
                {
                    indexes.Add(indexSpec.ToListIndexesBsonDocument());
                }
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


    private BsonDocument HandleDropIndexes(string database, BsonDocument command)
    {
        if (!command.TryGetValue("dropIndexes", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'dropIndexes' field.");

        if (!command.TryGetValue("index", out var indexValue))
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'index' field.");

        string collection = collValue.AsString;
        var collKey = (database, collection);

        lock (_lock)
        {
            var indexes = GetOrCreateIndexes(collKey);
            int numIndexesBefore = indexes.Count + 1; // +1 for _id

            if (indexValue.IsString && indexValue.AsString == "*")
            {
                // Drop all non-_id indexes
                indexes.Clear();
                _textIndexes.Remove(collKey);
                if (_stores.TryGetValue(collKey, out var allStore))
                    allStore.ClearUniqueIndexes();
                return new BsonDocument
                {
                    { "ok", 1.0 },
                    { "nIndexesWas", numIndexesBefore },
                    { "msg", "non-_id indexes dropped for collection" }
                };
            }

            var namesToDrop = new List<string>();
            if (indexValue.IsString)
            {
                namesToDrop.Add(indexValue.AsString);
            }
            else if (indexValue.IsBsonArray)
            {
                foreach (var item in (BsonArray)indexValue)
                {
                    if (!item.IsString)
                        throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Index names must be strings.");
                    namesToDrop.Add(item.AsString);
                }
            }
            else if (indexValue is BsonDocument keyDoc)
            {
                // Drop by key document - find matching index
                var spec = IndexSpec.TryCreate(new BsonDocument { { "key", keyDoc } });
                if (spec == null)
                    throw new MongoCommandException(ErrorCodes.IndexNotFound, "IndexNotFound", "can't find index with key like that");

                // Find index with matching key
                var matchingIndex = indexes.Values.FirstOrDefault(i =>
                    i.KeyDocument.ToJson() == keyDoc.ToJson()
                );
                if (matchingIndex == null)
                    throw new MongoCommandException(ErrorCodes.IndexNotFound, "IndexNotFound", "can't find index with key like that");

                namesToDrop.Add(matchingIndex.Name);
            }
            else
            {
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Invalid 'index' field.");
            }

            // Validate all names and drop them
            foreach (var name in namesToDrop)
            {
                if (name == "_id_")
                    throw new MongoCommandException(ErrorCodes.InvalidOptions, "InvalidOptions", "cannot drop _id index");

                if (!indexes.ContainsKey(name))
                    throw new MongoCommandException(ErrorCodes.IndexNotFound, "IndexNotFound", $"index not found with name [{name}]");

                // Check if it's a text index and maintain backward compat
                if (indexes[name].Type == IndexType.Text)
                    _textIndexes.Remove(collKey);

                indexes.Remove(name);
                if (_stores.TryGetValue(collKey, out var store))
                    store.RemoveUniqueIndex(name);
            }

            return new BsonDocument
            {
                { "ok", 1.0 },
                { "nIndexesWas", numIndexesBefore }
            };
        }
    }

    private BsonDocument HandleCreateIndexes(string database, BsonDocument command)
    {
        if (!command.TryGetValue("createIndexes", out var collValue) || !collValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing 'createIndexes' field.");

        if (!command.TryGetValue("indexes", out var indexesValue) || !indexesValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Missing or invalid 'indexes' field.");

        string collection = collValue.AsString;
        var indexesArray = (BsonArray)indexesValue;
        var collKey = (database, collection);

        lock (_lock)
        {
            var indexes = GetOrCreateIndexes(collKey);
            int numIndexesBefore = indexes.Count + 1; // +1 for _id
            int numIndexesAfter = numIndexesBefore;

            foreach (var indexElem in indexesArray)
            {
                if (indexElem is not BsonDocument indexDoc)
                    continue;

                var indexSpec = IndexSpec.TryCreate(indexDoc);
                if (indexSpec == null)
                    continue;

                // Check for existing index with same name
                if (indexes.TryGetValue(indexSpec.Name, out var existing))
                {
                    // Check if it's identical (idempotent)
                    if (existing.KeyDocument.ToJson() == indexSpec.KeyDocument.ToJson() &&
                        existing.Unique == indexSpec.Unique &&
                        existing.Sparse == indexSpec.Sparse &&
                        existing.ExpireAfterSeconds == indexSpec.ExpireAfterSeconds &&
                        existing.PartialFilterExpression?.ToJson() == indexSpec.PartialFilterExpression?.ToJson())
                    {
                        continue; // Already exists, no error
                    }

                    // Genuine conflict: two different indexes with same name
                    throw new MongoCommandException(
                        ErrorCodes.BadValue,
                        "BadValue",
                        $"index already exists with different options [{indexSpec.Name}]");
                }

                // Check for text index conflict (only one text index per collection)
                if (indexSpec.Type == IndexType.Text)
                {
                    var existingTextIndex = indexes.Values.FirstOrDefault(i => i.Type == IndexType.Text);
                    if (existingTextIndex != null && existingTextIndex.Name != indexSpec.Name)
                    {
                        throw new MongoCommandException(
                            ErrorCodes.BadValue,
                            "BadValue",
                            "Only one text index is allowed per collection.");
                    }
                }

                // Enforce a unique index from now on; existing duplicates make creation fail
                var store = GetOrCreateStore(collKey);
                if (indexSpec.Unique && indexSpec.Type != IndexType.Text)
                {
                    var error = store.AddUniqueIndex(indexSpec);
                    if (error != null)
                        throw error.ToException();
                }
                store.Exists = true;

                // Store the index
                indexes[indexSpec.Name] = indexSpec;

                // Also maintain _textIndexes for backward compatibility
                if (indexSpec.Type == IndexType.Text)
                {
                    var textIndexSpec = TextIndexSpec.TryCreate(indexSpec.KeyDocument);
                    if (textIndexSpec != null)
                        _textIndexes[collKey] = textIndexSpec;
                }

                numIndexesAfter++;
            }

            return new BsonDocument
            {
                { "ok", 1.0 },
                { "numIndexesBefore", numIndexesBefore },
                { "numIndexesAfter", numIndexesAfter }
            };
        }
    }

    private BsonDocument HandleFindAndModify(string database, BsonDocument command, CancellationToken ct)
    {
        lock (_lock)
        {
            string collection = GetCommandCollectionName(command, "findandmodify");
            var filter = command.TryGetValue("query", out var qValue) ? (BsonDocument)qValue : new BsonDocument();
            var sort = command.TryGetValue("sort", out var sValue) ? (BsonDocument)sValue : null;
            bool returnNew = command.TryGetValue("new", out var newValue) ? newValue.ToBoolean() : false;
            bool upsert = command.TryGetValue("upsert", out var upValue) ? upValue.ToBoolean() : false;

            bool isUpdate = command.Contains("update");
            bool isRemove = command.TryGetValue("remove", out var removeValue) && removeValue.ToBoolean();

            if (isRemove && (isUpdate || upsert))
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue",
                    "Cannot specify both remove and update/upsert in findAndModify");

            var collKey = (database, collection);
            var store = GetOrCreateStore(collKey);
            var data = Cancellable(store.CurrentDocuments(), ct);

            // maybe we don't have a link
            _textIndexes.TryGetValue(collKey, out var textIndex);

            var executor = new BsonQueryExecutor();
            var results = executor.ExecuteFind(data, filter, null, sort, 0, 1, textIndex).ToList();

            if (results.Count == 0)
            {
                if (isUpdate && upsert && command.TryGetValue("update", out var updateValue))
                {
                    var update = (BsonDocument)updateValue;
                    bool isOperatorUpdate = update.ElementCount > 0 && update.GetElement(0).Name.StartsWith("$");

                    BsonDocument newDoc;
                    if (isOperatorUpdate)
                    {
                        newDoc = new BsonDocument();
                        if (filter.Contains("_id"))
                            newDoc["_id"] = filter["_id"];
                        else
                            newDoc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();

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

                    var insertError = store.Insert(newDoc);
                    if (insertError != null)
                        throw insertError.ToException();
                    ChangeEvents.Publish("insert", database, collection, newDoc["_id"], fullDocument: newDoc);

                    return new BsonDocument
                    {
                        { "ok", 1.0 },
                        { "value", returnNew ? newDoc : BsonNull.Value }
                    };
                }

                return new BsonDocument { { "ok", 1.0 }, { "value", BsonNull.Value } };
            }

            var foundDoc = results[0];
            var returnValue = new BsonDocument(foundDoc);
            BsonDocument? updatedDoc = null;

            if (isUpdate && command.TryGetValue("update", out var updateValue2))
            {
                var update = (BsonDocument)updateValue2;
                bool isOperatorUpdate = update.ElementCount > 0 && update.GetElement(0).Name.StartsWith("$");

                store.TryGet(foundDoc["_id"], out var snapshot);
                BsonDocument newDoc;
                if (isOperatorUpdate)
                {
                    newDoc = UpdateApplier.ApplyOperators(foundDoc, update);
                }
                else
                {
                    newDoc = new BsonDocument(update);
                    if (!newDoc.Contains("_id"))
                        newDoc.InsertAt(0, new BsonElement("_id", foundDoc["_id"]));
                }

                var replaceError = store.Replace(snapshot, newDoc);
                if (replaceError != null)
                    throw replaceError.ToException();
                PublishUpdate(database, collection, foundDoc, newDoc, isOperatorUpdate);
                updatedDoc = newDoc;
            }
            else if (isRemove)
            {
                store.Remove(foundDoc["_id"]);
                ChangeEvents.Publish("delete", database, collection, foundDoc["_id"], fullDocumentBeforeChange: foundDoc);
            }

            BsonValue valueToReturn = isUpdate && returnNew && updatedDoc != null
                ? updatedDoc
                : returnValue;

            return new BsonDocument
            {
                { "ok", 1.0 },
                { "value", valueToReturn }
            };
        }
    }

    private void PublishUpdate(string database, string collection, BsonDocument oldDoc, BsonDocument newDoc, bool isOperatorUpdate)
    {
        if (!isOperatorUpdate)
        {
            ChangeEvents.Publish("replace", database, collection, newDoc["_id"], fullDocument: newDoc, fullDocumentBeforeChange: oldDoc);
            return;
        }

        var updatedFields = new BsonDocument();
        foreach (var element in newDoc)
        {
            if (!oldDoc.TryGetValue(element.Name, out var oldValue) || !oldValue.Equals(element.Value))
                updatedFields[element.Name] = element.Value;
        }

        var removedFields = new BsonArray(oldDoc.Names.Where(name => !newDoc.Contains(name)));
        var updateDescription = new BsonDocument
        {
            { "updatedFields", updatedFields },
            { "removedFields", removedFields },
            { "truncatedArrays", new BsonArray() }
        };

        ChangeEvents.Publish("update", database, collection, newDoc["_id"], fullDocument: newDoc,
            fullDocumentBeforeChange: oldDoc, updateDescription: updateDescription);
    }
}
