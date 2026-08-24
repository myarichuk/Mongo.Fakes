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
            if (current is BsonArray array)
            {
                if (int.TryParse(part, out int index) && index >= 0 && index < array.Count)
                {
                    current = array[index];
                }
                else
                {
                    var found = false;
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (array[i] is BsonDocument subdoc && subdoc.TryGetValue(part, out var value))
                        {
                            current = value;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        return null;
                }
            }
            else if (current is BsonDocument bdoc && bdoc.TryGetValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    public static void SetValueByPath(BsonDocument doc, string path, BsonValue value)
    {
        var parts = path.Split('.');
        BsonDocument current = doc;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (!current.TryGetValue(part, out var next))
            {
                next = new BsonDocument();
                current[part] = next;
            }

            if (next is not BsonDocument nextDoc)
                nextDoc = new BsonDocument();

            current[part] = nextDoc;
            current = nextDoc;
        }

        current[parts[^1]] = value;
    }

    public static void RemoveValueByPath(BsonDocument doc, string path)
    {
        var parts = path.Split('.');
        BsonDocument current = doc;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) || next is not BsonDocument nextDoc)
                return;

            current = nextDoc;
        }

        current.Remove(parts[^1]);
    }
}
