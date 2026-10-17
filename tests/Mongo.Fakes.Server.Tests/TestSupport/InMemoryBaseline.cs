using MongoDB.Bson;

namespace Mongo.Fakes.Server.Tests.TestSupport;

/// <summary>Fixture data held in memory, with an optional delay on each read to simulate slow loads.</summary>
internal sealed class InMemoryBaseline : IBaselineDataProvider
{
    private readonly Dictionary<(string, string), List<BsonDocument>> _collections = new();

    public TimeSpan Delay { get; set; }

    public InMemoryBaseline Add(string database, string collection, params BsonDocument[] docs)
    {
        if (!_collections.TryGetValue((database, collection), out var list))
            _collections[(database, collection)] = list = new List<BsonDocument>();
        list.AddRange(docs);
        return this;
    }

    public IReadOnlyList<BsonDocument> GetCollection(string database, string collection)
    {
        if (Delay > TimeSpan.Zero)
            Thread.Sleep(Delay);
        return _collections.TryGetValue((database, collection), out var list) ? list : [];
    }

    public IReadOnlyList<string> GetDatabases() => _collections.Keys.Select(k => k.Item1).Distinct().ToList();

    public IReadOnlyList<string> GetCollections(string database) =>
        _collections.Keys.Where(k => k.Item1 == database).Select(k => k.Item2).ToList();
}
