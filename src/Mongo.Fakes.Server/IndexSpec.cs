using MongoDB.Bson;

namespace Mongo.Fakes.Server;

/// <summary>
/// Index type classification.
/// </summary>
public enum IndexType
{
    Text,
    SingleField,
    Compound,
    Hashed,
    Geospatial2D,
    Geospatial2DSphere,
    Other
}

/// <summary>
/// Specification for any type of MongoDB index: text, single-field, compound, etc.
/// </summary>
public record IndexSpec(
    string Name,
    BsonDocument KeyDocument,
    IndexType Type,
    bool Unique = false,
    bool Sparse = false,
    int? ExpireAfterSeconds = null)
{

    /// <summary>
    /// Try to create an IndexSpec from a createIndexes command index document.
    /// Returns null if the document is invalid or unsupported.
    /// </summary>
    public static IndexSpec? TryCreate(BsonDocument indexDoc)
    {
        if (!indexDoc.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument keyDoc)
            return null;

        if (keyDoc.ElementCount == 0)
            return null;

        // Extract options
        bool unique = indexDoc.TryGetValue("unique", out var uVal) && uVal.ToBoolean();
        bool sparse = indexDoc.TryGetValue("sparse", out var sVal) && sVal.ToBoolean();
        int? expireAfterSeconds = null;
        if (indexDoc.TryGetValue("expireAfterSeconds", out var eVal))
        {
            expireAfterSeconds = eVal.ToInt32();
        }

        // Determine index type and generate name
        var (indexType, generatedName) = ClassifyIndexType(keyDoc);

        // Use provided name or generated one
        string name = indexDoc.TryGetValue("name", out var nVal) && nVal.IsString
            ? nVal.AsString
            : generatedName;

        return new IndexSpec(
            Name: name,
            KeyDocument: new BsonDocument(keyDoc),
            Type: indexType,
            Unique: unique,
            Sparse: sparse,
            ExpireAfterSeconds: expireAfterSeconds
        );
    }

    /// <summary>
    /// Classify the index type and generate a default name.
    /// </summary>
    private static (IndexType, string) ClassifyIndexType(BsonDocument keyDoc)
    {
        int fieldCount = keyDoc.ElementCount;
        var firstElement = keyDoc.GetElement(0);
        var firstValue = firstElement.Value;

        // Text index
        if (firstValue is BsonString { Value: "text" })
        {
            var textFields = new List<string>();
            foreach (var elem in keyDoc)
            {
                if (elem.Value is BsonString { Value: "text" })
                {
                    if (elem.Name != "$**")
                        textFields.Add(elem.Name);
                }
            }

            var name = textFields.Count > 0
                ? $"{string.Join("_", textFields)}_text"
                : "$**_text";

            return (IndexType.Text, name);
        }

        // Hashed index
        if (firstValue is BsonString { Value: "hashed" })
        {
            return (IndexType.Hashed, $"{firstElement.Name}_hashed");
        }

        // 2dsphere index
        if (firstValue is BsonString { Value: "2dsphere" })
        {
            return (IndexType.Geospatial2DSphere, $"{firstElement.Name}_2dsphere");
        }

        // 2d index
        if (firstValue is BsonString { Value: "2d" })
        {
            return (IndexType.Geospatial2D, $"{firstElement.Name}_2d");
        }

        // Check for geospatial in compound
        foreach (var elem in keyDoc)
        {
            if (elem.Value is BsonString { Value: "2dsphere" })
                return (IndexType.Geospatial2DSphere, GenerateCompoundName(keyDoc));
            if (elem.Value is BsonString { Value: "2d" })
                return (IndexType.Geospatial2D, GenerateCompoundName(keyDoc));
        }

        // Single field or compound (ascending/descending)
        if (fieldCount == 1)
        {
            var value = firstValue.ToInt32();
            var direction = value == 1 ? "1" : "-1";
            return (IndexType.SingleField, $"{firstElement.Name}_{direction}");
        }

        return (IndexType.Compound, GenerateCompoundName(keyDoc));
    }

    private static string GenerateCompoundName(BsonDocument keyDoc)
    {
        var parts = new List<string>();
        foreach (var elem in keyDoc)
        {
            var value = elem.Value;
            if (value is BsonString stringVal)
            {
                parts.Add($"{elem.Name}_{stringVal.Value}");
            }
            else if (value.IsInt32 || value.IsInt64)
            {
                var direction = value.ToInt32() == 1 ? "1" : "-1";
                parts.Add($"{elem.Name}_{direction}");
            }
            else
            {
                parts.Add(elem.Name);
            }
        }

        return string.Join("_", parts);
    }

    /// <summary>
    /// Convert to BsonDocument for listIndexes response.
    /// </summary>
    public BsonDocument ToListIndexesBsonDocument()
    {
        var doc = new BsonDocument
        {
            { "v", 2 },
            { "key", new BsonDocument(KeyDocument) },
            { "name", Name }
        };

        if (Type == IndexType.Text)
        {
            doc["default_language"] = "english";
            doc["textIndexVersion"] = 3;
        }

        if (Unique)
            doc["unique"] = true;

        if (Sparse)
            doc["sparse"] = true;

        if (ExpireAfterSeconds.HasValue)
            doc["expireAfterSeconds"] = ExpireAfterSeconds.Value;

        return doc;
    }
}
