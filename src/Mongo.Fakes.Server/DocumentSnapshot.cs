using MongoDB.Bson;

namespace Mongo.Fakes.Server;

internal sealed class DocumentSnapshot
{
    public BsonDocument Original { get; }
    public BsonDocument? Mutated { get; set; }

    public BsonDocument Current => Mutated ?? Original;
    public bool IsDirty => Mutated != null;

    public DocumentSnapshot(BsonDocument original)
    {
        Original = original;
    }
}
