using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Query;

internal sealed class Projector
{
    private readonly BsonDocument _projectionSpec;
    private readonly bool _isInclusion;
    private readonly bool _hasIdField;

    public Projector(BsonDocument projectionSpec)
    {
        _projectionSpec = projectionSpec;
        _hasIdField = projectionSpec.Contains("_id");

        bool? inclusionMode = null;
        foreach (var element in projectionSpec.Elements)
        {
            bool isInclusion = element.Value.ToBoolean();
            if (inclusionMode == null)
                inclusionMode = isInclusion;
            else if (inclusionMode != isInclusion && element.Name != "_id")
                throw new InvalidOperationException("Cannot mix inclusion and exclusion projections (except for _id).");
        }

        _isInclusion = inclusionMode ?? false;
    }

    public BsonDocument Project(BsonDocument doc)
    {
        var result = new BsonDocument();

        if (_isInclusion)
        {
            foreach (var element in _projectionSpec.Elements)
            {
                if (element.Name == "_id" && !element.Value.ToBoolean())
                    continue;

                if (element.Value.ToBoolean())
                {
                    var value = BsonPath.GetValue(doc, element.Name);
                    if (value != null)
                        result[element.Name] = value;
                }
            }

            if (!_hasIdField || _projectionSpec["_id"].ToBoolean())
            {
                if (doc.Contains("_id"))
                    result["_id"] = doc["_id"];
            }
        }
        else
        {
            result = new BsonDocument(doc);

            foreach (var element in _projectionSpec.Elements)
            {
                if (!element.Value.ToBoolean())
                {
                    if (element.Name == "_id")
                        result.Remove("_id");
                    else
                        RemoveFieldByPath(result, element.Name);
                }
            }

            if (!_hasIdField && result.Contains("_id"))
                result.Remove("_id");
        }

        return result;
    }

    private static void RemoveFieldByPath(BsonDocument doc, string path)
    {
        var parts = path.Split('.');
        BsonDocument current = doc;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var value) || !value.IsBsonDocument)
                return;
            current = (BsonDocument)value;
        }

        current.Remove(parts[^1]);
    }
}
