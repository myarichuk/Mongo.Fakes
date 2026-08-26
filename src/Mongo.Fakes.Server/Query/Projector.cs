using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Aggregation;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Query;

internal sealed class Projector
{
    private readonly BsonDocument _projectionSpec;
    private readonly bool _isInclusion;
    private readonly bool _hasIdField;

    // { field: { $meta: ... } } is an additive computed field in both find()-style projections
    // and aggregation $project stages: it does not restrict the result to only the specified
    // fields, unlike other computed fields, which are always restrictive.
    private static bool IsMetaField(BsonValue value) =>
        value.IsBsonDocument && value.AsBsonDocument.ElementCount == 1 &&
        value.AsBsonDocument.GetElement(0).Name == "$meta";

    private static bool IsFieldPathExpression(BsonValue value) =>
        value.IsString && value.AsString.StartsWith("$");

    public Projector(BsonDocument projectionSpec)
    {
        _projectionSpec = projectionSpec;
        _hasIdField = projectionSpec.Contains("_id");

        bool? inclusionMode = null;
        foreach (var element in projectionSpec.Elements)
        {
            if (element.Name == "_id")
                continue;

            bool isComputedField = element.Value.IsBsonDocument && element.Value.AsBsonDocument.ElementCount == 1 &&
                element.Value.AsBsonDocument.GetElement(0).Name.StartsWith("$");
            bool isFieldPathExpression = IsFieldPathExpression(element.Value);

            if (!isComputedField && !isFieldPathExpression && !element.Value.IsBoolean && !element.Value.IsNumeric && !element.Value.IsString)
                throw new NotSupportedException("computed projection fields are not supported");

            if (IsMetaField(element.Value))
                continue;

            bool isInclusion = isComputedField || isFieldPathExpression || element.Value.ToBoolean();
            if (inclusionMode == null)
                inclusionMode = isInclusion;
            else if (inclusionMode != isInclusion)
                throw new ArgumentException("Cannot mix inclusion and exclusion projections.");
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

                bool isComputedField = element.Value.IsBsonDocument && element.Value.AsBsonDocument.ElementCount == 1 &&
                    element.Value.AsBsonDocument.GetElement(0).Name.StartsWith("$");
                bool isFieldPathExpression = IsFieldPathExpression(element.Value);

                if (isComputedField)
                {
                    var computedValue = ExpressionEvaluator.Evaluate(element.Value, doc);
                    BsonPath.SetValueByPath(result, element.Name, computedValue);
                }
                else if (isFieldPathExpression)
                {
                    var fieldPathValue = ExpressionEvaluator.Evaluate(element.Value, doc);
                    if (fieldPathValue != null)
                        BsonPath.SetValueByPath(result, element.Name, fieldPathValue);
                }
                else if (element.Value.ToBoolean())
                {
                    var value = BsonPath.GetValue(doc, element.Name);
                    if (value != null)
                        BsonPath.SetValueByPath(result, element.Name, value);
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
                bool isComputedField = element.Value.IsBsonDocument && element.Value.AsBsonDocument.ElementCount == 1 &&
                    element.Value.AsBsonDocument.GetElement(0).Name.StartsWith("$");
                bool isFieldPathExpression = IsFieldPathExpression(element.Value);

                if (isComputedField || isFieldPathExpression)
                {
                    var computedValue = ExpressionEvaluator.Evaluate(element.Value, doc);
                    BsonPath.SetValueByPath(result, element.Name, computedValue);
                }
                else if (!element.Value.ToBoolean())
                {
                    if (element.Name == "_id")
                        result.Remove("_id");
                    else
                        BsonPath.RemoveValueByPath(result, element.Name);
                }
            }
        }

        return result;
    }
}
