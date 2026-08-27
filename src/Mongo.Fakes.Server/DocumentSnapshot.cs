using MongoDB.Bson;

namespace Mongo.Fakes.Server;

public sealed class DocumentSnapshot
{
    public BsonDocument Original { get; }
    public BsonDocument? Mutated { get; set; }
    public string? InstanceName { get; }

    public BsonDocument Current => Mutated ?? Original;
    public bool IsDirty => Mutated != null;

    public DocumentSnapshot(BsonDocument original, string? instanceName = null)
    {
        Original = original;
        InstanceName = instanceName;
    }

    public string GetDebugInfo()
    {
        var state = IsDirty ? "mutated" : "baseline";
        var id = Original.TryGetValue("_id", out var idValue) ? idValue : "unknown";
        var name = InstanceName ?? "unnamed";
        return $"Snapshot[{name}] (id={id}, state={state})";
    }
}
