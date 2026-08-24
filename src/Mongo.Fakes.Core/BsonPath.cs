using MongoDB.Bson;

namespace Mongo.Fakes.Core;

/// <summary>
/// Dot-notation field access over <see cref="BsonDocument"/>. Distinguishes a missing
/// field (returns <c>null</c>) from a field whose value is BSON null
/// (returns <see cref="BsonNull.Value"/>), which is load-bearing for $eq/$exists semantics.
/// </summary>
public static class BsonPath
{
    public static BsonValue? GetValue(BsonDocument doc, string path)
    {
        var parts = path.Split('.');
        BsonValue current = doc;

        foreach (var part in parts)
        {
            if (current is not BsonDocument bdoc || !bdoc.TryGetValue(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
