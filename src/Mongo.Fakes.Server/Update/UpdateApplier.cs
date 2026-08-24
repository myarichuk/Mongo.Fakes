using MongoDB.Bson;
using Mongo.Fakes.Core;

namespace Mongo.Fakes.Server.Update;

internal sealed class UpdateApplier
{
    public static BsonDocument ApplyOperators(BsonDocument doc, BsonDocument operators)
    {
        var result = (BsonDocument)doc.DeepClone();

        foreach (var element in operators.Elements)
        {
            var op = element.Name;
            var spec = element.Value;

            switch (op)
            {
                case "$set":
                    ApplySet(result, (BsonDocument)spec);
                    break;
                case "$unset":
                    ApplyUnset(result, (BsonDocument)spec);
                    break;
                case "$inc":
                    ApplyInc(result, (BsonDocument)spec);
                    break;
                case "$mul":
                    ApplyMul(result, (BsonDocument)spec);
                    break;
                case "$push":
                    ApplyPush(result, (BsonDocument)spec);
                    break;
                case "$pull":
                    ApplyPull(result, (BsonDocument)spec);
                    break;
                case "$addToSet":
                    ApplyAddToSet(result, (BsonDocument)spec);
                    break;
                case "$rename":
                    ApplyRename(result, (BsonDocument)spec);
                    break;
                case "$setOnInsert":
                    break;
                default:
                    throw new NotSupportedException($"Unsupported update operator: {op}");
            }
        }

        return result;
    }

    private static void ApplySet(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            BsonPath.SetValueByPath(doc, element.Name, element.Value);
        }
    }

    private static void ApplyUnset(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            BsonPath.RemoveValueByPath(doc, element.Name);
        }
    }

    private static void ApplyInc(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            if (current == null || current.BsonType == BsonType.Null)
            {
                BsonPath.SetValueByPath(doc, element.Name, element.Value);
            }
            else if (current.IsNumeric && element.Value.IsNumeric)
            {
                var sum = current.ToDouble() + element.Value.ToDouble();
                BsonValue result = current.IsInt32 && element.Value.IsInt32
                    ? new BsonInt32((int)(current.ToInt32() + element.Value.ToInt32()))
                    : new BsonDouble(sum);
                BsonPath.SetValueByPath(doc, element.Name, result);
            }
            else
            {
                throw new NotSupportedException($"Cannot increment non-numeric value");
            }
        }
    }

    private static void ApplyMul(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            if (current == null || current.BsonType == BsonType.Null)
            {
                BsonPath.SetValueByPath(doc, element.Name, new BsonInt32(0));
            }
            else if (current.IsNumeric && element.Value.IsNumeric)
            {
                var product = current.ToDouble() * element.Value.ToDouble();
                BsonValue result = new BsonDouble(product);
                BsonPath.SetValueByPath(doc, element.Name, result);
            }
            else
            {
                throw new NotSupportedException($"Cannot multiply non-numeric value");
            }
        }
    }

    private static void ApplyPush(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            BsonArray array;

            if (current == null || current.BsonType == BsonType.Null)
            {
                array = new BsonArray { element.Value };
                BsonPath.SetValueByPath(doc, element.Name, array);
            }
            else if (current.IsBsonArray)
            {
                array = (BsonArray)current;
                array.Add(element.Value);
            }
            else
            {
                throw new NotSupportedException($"Cannot push to non-array field");
            }
        }
    }

    private static void ApplyPull(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            if (current is BsonArray array)
            {
                var toRemove = new List<int>();
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i].CompareTo(element.Value) == 0)
                        toRemove.Add(i);
                }

                for (int i = toRemove.Count - 1; i >= 0; i--)
                    array.RemoveAt(toRemove[i]);
            }
        }
    }

    private static void ApplyAddToSet(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            BsonArray array;

            if (current == null || current.BsonType == BsonType.Null)
            {
                array = new BsonArray { element.Value };
                BsonPath.SetValueByPath(doc, element.Name, array);
            }
            else if (current.IsBsonArray)
            {
                array = (BsonArray)current;
                var exists = array.Any(v => v.CompareTo(element.Value) == 0);
                if (!exists)
                    array.Add(element.Value);
            }
            else
            {
                throw new NotSupportedException($"Cannot addToSet to non-array field");
            }
        }
    }

    private static void ApplyRename(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id" || !element.Value.IsString)
                throw new NotSupportedException("Invalid rename specification");

            var oldName = element.Name;
            var newName = element.Value.AsString;

            if (newName == "_id")
                throw new NotSupportedException("Cannot rename to _id");

            var value = BsonPath.GetValue(doc, oldName);
            if (value != null)
            {
                BsonPath.SetValueByPath(doc, newName, value);
                BsonPath.RemoveValueByPath(doc, oldName);
            }
        }
    }
}
