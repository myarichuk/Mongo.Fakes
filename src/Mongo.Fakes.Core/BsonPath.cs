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
        BsonValue current = doc;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];

            if (current is BsonArray array)
            {
                if (int.TryParse(part, out int index) && index >= 0)
                {
                    while (array.Count <= index)
                        array.Add(BsonNull.Value);

                    if (array[index].BsonType == BsonType.Null || array[index] is not BsonDocument)
                        array[index] = new BsonDocument();

                    current = array[index];
                }
                else
                {
                    for (int j = 0; j < array.Count; j++)
                    {
                        if (array[j] is BsonDocument subdoc)
                        {
                            if (!subdoc.TryGetValue(part, out _))
                                subdoc[part] = new BsonDocument();

                            current = subdoc[part];
                            break;
                        }
                    }
                }
            }
            else if (current is BsonDocument bdoc)
            {
                if (!bdoc.TryGetValue(part, out var next))
                {
                    next = new BsonDocument();
                    bdoc[part] = next;
                }

                if (next is not BsonDocument nextDoc)
                    nextDoc = new BsonDocument();

                bdoc[part] = nextDoc;
                current = nextDoc;
            }
        }

        var lastPart = parts[^1];
        if (current is BsonArray lastArray)
        {
            if (int.TryParse(lastPart, out int lastIndex) && lastIndex >= 0)
            {
                while (lastArray.Count <= lastIndex)
                    lastArray.Add(BsonNull.Value);

                lastArray[lastIndex] = value;
            }
        }
        else if (current is BsonDocument lastDoc)
        {
            lastDoc[lastPart] = value;
        }
    }

    public static void RemoveValueByPath(BsonDocument doc, string path)
    {
        var parts = path.Split('.');
        BsonValue current = doc;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];

            if (current is BsonArray array)
            {
                if (int.TryParse(part, out int index) && index >= 0 && index < array.Count)
                {
                    current = array[index];
                }
                else
                {
                    var found = false;
                    for (int j = 0; j < array.Count; j++)
                    {
                        if (array[j] is BsonDocument subdoc && subdoc.TryGetValue(part, out var value))
                        {
                            current = value;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        return;
                }
            }
            else if (current is BsonDocument bdoc && bdoc.TryGetValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return;
            }
        }

        var lastPart = parts[^1];
        if (current is BsonArray lastArray)
        {
            if (int.TryParse(lastPart, out int lastIndex) && lastIndex >= 0 && lastIndex < lastArray.Count)
                lastArray.RemoveAt(lastIndex);
        }
        else if (current is BsonDocument lastDoc)
        {
            lastDoc.Remove(lastPart);
        }
    }
}
