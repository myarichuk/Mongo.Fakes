using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

/// <summary>
/// Helper for computing and materializing group keys in aggregation stages.
/// </summary>
internal static class GroupKeyHelper
{
    /// <summary>
    /// Computes a group key string from an ID specification and document.
    /// Used by both $group and $setWindowFields for consistent partitioning.
    /// </summary>
    public static string ComputeKeyString(BsonValue idSpec, BsonDocument doc)
    {
        if (idSpec.BsonType == BsonType.Null)
            return "null|null";

        if (idSpec.IsString && idSpec.AsString.StartsWith("$"))
        {
            var value = BsonPath.GetValue(doc, idSpec.AsString[1..]) ?? BsonNull.Value;
            return $"{value.BsonType}|{value.ToJson()}";
        }

        if (idSpec.IsInt32 || idSpec.IsInt64 || idSpec.IsDouble || idSpec.IsBoolean)
            return $"{idSpec.BsonType}|{idSpec.ToJson()}";

        if (idSpec.IsBsonDocument)
        {
            var spec = (BsonDocument)idSpec;
            var keyParts = new List<string>();
            foreach (var elem in spec.Elements)
            {
                if (elem.Value.IsString && elem.Value.AsString.StartsWith("$"))
                {
                    var fieldValue = BsonPath.GetValue(doc, elem.Value.AsString[1..]) ?? BsonNull.Value;
                    keyParts.Add($"{elem.Name}:{fieldValue.BsonType}:{fieldValue.ToJson()}");
                }
                else if (elem.Value.IsString && elem.Value.AsString.StartsWith("$"))
                {
                    throw new NotSupportedException($"unsupported operator in group key: {elem.Value.AsString}");
                }
                else
                {
                    keyParts.Add($"{elem.Name}:{elem.Value.BsonType}:{elem.Value.ToJson()}");
                }
            }
            return string.Join("|", keyParts);
        }

        var specStr = idSpec.ToString();
        if (specStr != null && specStr.StartsWith("$"))
            throw new NotSupportedException($"unsupported operator in group key: {specStr}");

        return $"{idSpec.BsonType}|{idSpec.ToJson()}";
    }

    /// <summary>
    /// Materializes an _id value from a specification and representative document.
    /// Used when building result documents with group keys.
    /// </summary>
    public static BsonValue MaterializeKeyValue(BsonValue idSpec, BsonDocument representativeDoc)
    {
        if (idSpec.BsonType == BsonType.Null)
            return BsonNull.Value;

        if (idSpec.IsString && idSpec.AsString.StartsWith("$"))
            return BsonPath.GetValue(representativeDoc, idSpec.AsString[1..]) ?? BsonNull.Value;

        if (idSpec.IsBsonDocument)
        {
            var idDoc = new BsonDocument();
            var spec = (BsonDocument)idSpec;
            foreach (var elem in spec.Elements)
            {
                if (elem.Value.IsString && elem.Value.AsString.StartsWith("$"))
                    idDoc[elem.Name] = BsonPath.GetValue(representativeDoc, elem.Value.AsString[1..]) ?? BsonNull.Value;
                else
                    idDoc[elem.Name] = elem.Value;
            }
            return idDoc;
        }

        return idSpec;
    }
}
