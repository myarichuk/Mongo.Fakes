using MongoDB.Bson;
using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Query;

namespace Mongo.Fakes.Server;

/// <summary>
/// A failed write: a duplicate key, or an attempt to change <c>_id</c>.
/// </summary>
internal sealed record StoreError(int Code, string CodeName, string Message, BsonDocument? KeyPattern = null, BsonDocument? KeyValue = null)
{
    public BsonDocument ToWriteError(int index)
    {
        var doc = new BsonDocument
        {
            { "index", index },
            { "code", Code },
            { "errmsg", Message }
        };
        if (KeyPattern != null)
            doc["keyPattern"] = KeyPattern;
        if (KeyValue != null)
            doc["keyValue"] = KeyValue;
        return doc;
    }

    public MongoCommandException ToException()
    {
        BsonDocument? details = null;
        if (KeyPattern != null && KeyValue != null)
            details = new BsonDocument { { "keyPattern", KeyPattern }, { "keyValue", KeyValue } };
        return new MongoCommandException(Code, CodeName, Message, details);
    }
}

/// <summary>
/// The live documents of one collection, in natural (insertion) order, plus the key maps of its
/// unique indexes. The fixture baseline is copied in once, when the collection is first touched,
/// so every check sees fixture and inserted documents alike and a delete really removes the key.
/// Not thread-safe: <see cref="BsonFileBackend"/> serialises access under its lock.
/// </summary>
internal sealed class CollectionStore
{
    private static readonly BsonDocument IdKeyPattern = new() { { "_id", 1 } };

    private string _namespace;
    private readonly LinkedList<DocumentSnapshot> _order = new();
    private readonly Dictionary<BsonValue, LinkedListNode<DocumentSnapshot>> _byId = new(BsonKeyComparer.Instance);
    private readonly Dictionary<string, UniqueIndex> _uniqueIndexes = new();
    private BsonDocument[]? _currentCache;

    /// <param name="existsInBaseline">Whether the fixtures define the collection, even with no documents.</param>
    public CollectionStore(string database, string collection, IEnumerable<BsonDocument> baseline, bool existsInBaseline = false)
    {
        _namespace = $"{database}.{collection}";
        foreach (var doc in baseline)
        {
            if (!doc.Contains("_id"))
                doc["_id"] = ObjectId.GenerateNewId();
            if (!_byId.ContainsKey(doc["_id"]))
                _byId[doc["_id"]] = _order.AddLast(new DocumentSnapshot(doc));
        }
        Exists = existsInBaseline || _order.Count > 0;
    }

    /// <summary>A collection that has been dropped: empty, and hidden from <c>listCollections</c>.</summary>
    public static CollectionStore Dropped(string database, string collection) => new(database, collection, []);

    /// <summary>Points this store at a new namespace, for <c>renameCollection</c>.</summary>
    public void Rename(string database, string collection)
    {
        _namespace = $"{database}.{collection}";
        foreach (var index in _uniqueIndexes.Values)
            index.Namespace = _namespace;
    }

    /// <summary>The BSON size of every document, for <c>dbStats</c> and <c>collStats</c>.</summary>
    public long DataSize()
    {
        long size = 0;
        foreach (var snapshot in _order)
            size += snapshot.Current.ToBson().Length;
        return size;
    }

    /// <summary>Whether the collection exists as far as <c>listCollections</c> is concerned.</summary>
    public bool Exists { get; set; }

    public int Count => _order.Count;

    public IEnumerable<DocumentSnapshot> Snapshots => _order;

    /// <summary>
    /// The current documents. The array is cached and replaced (never modified) on writes,
    /// so a reader can keep using it outside the backend lock.
    /// </summary>
    public IReadOnlyList<BsonDocument> CurrentDocuments()
    {
        if (_currentCache == null)
        {
            var docs = new BsonDocument[_order.Count];
            int i = 0;
            foreach (var snapshot in _order)
                docs[i++] = snapshot.Current;
            _currentCache = docs;
        }
        return _currentCache;
    }

    public bool TryGet(BsonValue id, out DocumentSnapshot snapshot)
    {
        if (_byId.TryGetValue(id, out var node))
        {
            snapshot = node.Value;
            return true;
        }
        snapshot = null!;
        return false;
    }

    public StoreError? Insert(BsonDocument doc)
    {
        if (!doc.Contains("_id"))
            doc["_id"] = ObjectId.GenerateNewId();

        var id = doc["_id"];
        if (_byId.ContainsKey(id))
            return DuplicateKey("_id_", IdKeyPattern, new BsonDocument("_id", id));

        var pendingKeys = new List<(UniqueIndex Index, List<BsonArray> Keys)>();
        foreach (var index in _uniqueIndexes.Values)
        {
            var keys = index.KeysOf(doc);
            var error = index.FindConflict(keys, id);
            if (error != null)
                return error;
            pendingKeys.Add((index, keys));
        }

        _byId[id] = _order.AddLast(new DocumentSnapshot(doc));
        foreach (var (index, keys) in pendingKeys)
            index.Add(keys, id);

        Exists = true;
        _currentCache = null;
        return null;
    }

    public StoreError? Replace(DocumentSnapshot snapshot, BsonDocument newDoc)
    {
        var oldDoc = snapshot.Current;
        var id = oldDoc["_id"];

        if (!newDoc.TryGetValue("_id", out var newId))
            newDoc.InsertAt(0, new BsonElement("_id", id));
        else if (!BsonKeyComparer.Instance.Equals(id, newId))
            return new StoreError(ErrorCodes.ImmutableField, "ImmutableField",
                "Performing an update on the path '_id' would modify the immutable field '_id'");

        var changes = new List<(UniqueIndex Index, List<BsonArray> OldKeys, List<BsonArray> NewKeys)>();
        foreach (var index in _uniqueIndexes.Values)
        {
            var newKeys = index.KeysOf(newDoc);
            var error = index.FindConflict(newKeys, id);
            if (error != null)
                return error;
            changes.Add((index, index.KeysOf(oldDoc), newKeys));
        }

        foreach (var (index, oldKeys, newKeys) in changes)
        {
            index.Remove(oldKeys, id);
            index.Add(newKeys, id);
        }

        snapshot.Mutated = newDoc;
        _currentCache = null;
        return null;
    }

    public bool Remove(BsonValue id)
    {
        if (!_byId.Remove(id, out var node))
            return false;

        foreach (var index in _uniqueIndexes.Values)
            index.Remove(index.KeysOf(node.Value.Current), id);

        _order.Remove(node);
        _currentCache = null;
        return true;
    }

    /// <summary>
    /// Starts enforcing a unique index. Fails, like a real server, when the existing
    /// documents already hold a duplicate key.
    /// </summary>
    public StoreError? AddUniqueIndex(IndexSpec spec)
    {
        var index = new UniqueIndex(spec, _namespace);
        foreach (var snapshot in _order)
        {
            var id = snapshot.Current["_id"];
            var keys = index.KeysOf(snapshot.Current);
            var error = index.FindConflict(keys, id);
            if (error != null)
                return error;
            index.Add(keys, id);
        }

        _uniqueIndexes[spec.Name] = index;
        Exists = true;
        return null;
    }

    public void RemoveUniqueIndex(string name) => _uniqueIndexes.Remove(name);

    public void ClearUniqueIndexes() => _uniqueIndexes.Clear();

    private StoreError DuplicateKey(string indexName, BsonDocument keyPattern, BsonDocument keyValue) =>
        CreateDuplicateKeyError(_namespace, indexName, keyPattern, keyValue);

    private static StoreError CreateDuplicateKeyError(string ns, string indexName, BsonDocument keyPattern, BsonDocument keyValue) =>
        new(ErrorCodes.DuplicateKey, "DuplicateKey",
            $"E11000 duplicate key error collection: {ns} index: {indexName} dup key: {keyValue.ToJson()}",
            keyPattern, keyValue);

    private sealed class UniqueIndex
    {
        private readonly IndexSpec _spec;
        private readonly string[] _fields;
        private readonly Func<BsonDocument, bool>? _partialFilter;
        private readonly Dictionary<BsonValue, BsonValue> _owners = new(BsonKeyComparer.Instance);

        public UniqueIndex(IndexSpec spec, string ns)
        {
            _spec = spec;
            Namespace = ns;
            _fields = spec.KeyDocument.Names.ToArray();
            if (spec.PartialFilterExpression != null)
                _partialFilter = new FilterCompiler().Compile(spec.PartialFilterExpression);
        }

        public string Namespace { get; set; }

        /// <summary>
        /// The index keys a document produces: none when a sparse or partial index skips it,
        /// one per array element for a multikey field, and <c>null</c> for a missing field.
        /// </summary>
        public List<BsonArray> KeysOf(BsonDocument doc)
        {
            if (_partialFilter != null && !_partialFilter(doc))
                return [];

            var perField = new List<List<BsonValue>>(_fields.Length);
            bool anyPresent = false;
            foreach (var field in _fields)
            {
                var value = BsonPath.GetValue(doc, field);
                if (value == null)
                {
                    perField.Add([BsonNull.Value]);
                    continue;
                }

                anyPresent = true;
                perField.Add(value is BsonArray array ? Flatten(array) : [value]);
            }

            if (_spec.Sparse && !anyPresent)
                return [];

            var keys = new List<BsonArray> { new() };
            foreach (var values in perField)
            {
                var next = new List<BsonArray>(keys.Count * values.Count);
                foreach (var prefix in keys)
                {
                    foreach (var value in values)
                    {
                        var key = new BsonArray(prefix) { value };
                        next.Add(key);
                    }
                }
                keys = next;
            }

            // A document may repeat a value in a multikey field; that's not a conflict with itself.
            return keys.Distinct<BsonArray>(BsonKeyComparer.Instance).ToList();
        }

        public StoreError? FindConflict(List<BsonArray> keys, BsonValue id)
        {
            foreach (var key in keys)
            {
                if (_owners.TryGetValue(key, out var owner) && !BsonKeyComparer.Instance.Equals(owner, id))
                    return CreateDuplicateKeyError(Namespace, _spec.Name, KeyPattern(), KeyValue(key));
            }
            return null;
        }

        public void Add(List<BsonArray> keys, BsonValue id)
        {
            foreach (var key in keys)
                _owners[key] = id;
        }

        public void Remove(List<BsonArray> keys, BsonValue id)
        {
            foreach (var key in keys)
            {
                if (_owners.TryGetValue(key, out var owner) && BsonKeyComparer.Instance.Equals(owner, id))
                    _owners.Remove(key);
            }
        }

        private BsonDocument KeyPattern() => new(_spec.KeyDocument);

        private BsonDocument KeyValue(BsonArray key)
        {
            var doc = new BsonDocument();
            for (int i = 0; i < _fields.Length; i++)
                doc[_fields[i]] = key[i];
            return doc;
        }

        private static List<BsonValue> Flatten(BsonArray array)
        {
            // An empty array is indexed as undefined; it can't collide with null or with a value.
            if (array.Count == 0)
                return [BsonUndefined.Value];

            var values = new List<BsonValue>();
            foreach (var item in array)
            {
                // BsonPath collects "a.b" over an array of subdocuments into an array of arrays.
                if (item is BsonArray inner)
                    values.AddRange(inner);
                else
                    values.Add(item);
            }
            return values;
        }
    }
}
