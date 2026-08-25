using MongoDB.Bson;

namespace Mongo.Fakes.Server;

public sealed class FileBasedBaselineProvider : IBaselineDataProvider
{
    private readonly Dictionary<string, Dictionary<string, List<BsonDocument>>> _databases;
    private readonly object _lock = new();

    public FileBasedBaselineProvider(string fixtureRootFolder)
    {
        _databases = LoadAllFixtures(fixtureRootFolder);
    }

    public FileBasedBaselineProvider(string fixtureRootFolder, bool loadFromMongoDump)
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
                return docs;
            return [];
        }
    }

    public IReadOnlyList<string> GetDatabases()
    {
        lock (_lock)
        {
            return _databases.Keys.ToList();
        }
    }

    public IReadOnlyList<string> GetCollections(string database)
    {
        lock (_lock)
        {
            if (_databases.TryGetValue(database, out var db))
                return db.Keys.ToList();
            return [];
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

    private static List<BsonDocument> LoadJsonFile(string path)
    {
        var docs = new List<BsonDocument>();
        var content = File.ReadAllText(path);

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("["))
        {
            var array = BsonDocument.Parse("{ arr: " + content + " }")["arr"].AsBsonArray;
            foreach (var element in array)
            {
                var doc = element.IsBsonDocument ? (BsonDocument)element : new BsonDocument { { "value", element } };
                if (!doc.Contains("_id"))
                    doc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();

                docs.Add(doc);
            }
        }
        else
        {
            foreach (var line in content.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var doc = BsonDocument.Parse(line);
                if (!doc.Contains("_id"))
                    doc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();

                docs.Add(doc);
            }
        }

        return docs;
    }

    private static Dictionary<string, Dictionary<string, List<BsonDocument>>> LoadFromMongoDump(string rootFolder)
    {
        var databases = new Dictionary<string, Dictionary<string, List<BsonDocument>>>();

        if (!Directory.Exists(rootFolder))
            return databases;

        foreach (var dbDir in Directory.EnumerateDirectories(rootFolder))
        {
            var dbName = Path.GetFileName(dbDir);
            var collections = new Dictionary<string, List<BsonDocument>>();

            var collectionNames = new HashSet<string>();
            foreach (var file in Directory.EnumerateFiles(dbDir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".metadata.json"))
                    continue;

                var ext = Path.GetExtension(file).ToLowerInvariant();
                var collectionName = Path.GetFileNameWithoutExtension(file);

                if (ext == ".bson" && !collectionNames.Contains(collectionName))
                {
                    collections[collectionName] = LoadBsonFile(file);
                    collectionNames.Add(collectionName);
                }
                else if (ext == ".json" && !collectionNames.Contains(collectionName))
                {
                    collections[collectionName] = LoadJsonFile(file);
                    collectionNames.Add(collectionName);
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
                        if (!doc.Contains("_id"))
                            doc["_id"] = MongoDB.Bson.ObjectId.GenerateNewId();
                        documents.Add(doc);
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Error deserializing BSON document from {path} at offset {stream.Position}", ex);
                }
            }
        }

        return documents;
    }
}
