using MongoDB.Bson;
using Mongo.Fakes.Core;

namespace Mongo.Fakes.Server.Update;

internal sealed class UpdateApplier
{
    public static BsonDocument ApplyOperators(BsonDocument doc, BsonDocument operators, bool isUpsertInsert = false)
    {
        var result = (BsonDocument)doc.DeepClone();

        foreach (var element in operators.Elements)
        {
            var op = element.Name;
            var spec = element.Value;

            switch (op)
            {
                case "$set":
                    ApplySet(result, (BsonDocument)spec, allowIdModification: false);
                    break;
                case "$unset":
                    ApplyUnset(result, (BsonDocument)spec, allowIdModification: false);
                    break;
                case "$inc":
                    ApplyInc(result, (BsonDocument)spec, allowIdModification: false);
                    break;
                case "$mul":
                    ApplyMul(result, (BsonDocument)spec, allowIdModification: false);
                    break;
                case "$min":
                    ApplyMinMax(result, (BsonDocument)spec, min: true, allowIdModification: false);
                    break;
                case "$max":
                    ApplyMinMax(result, (BsonDocument)spec, min: false, allowIdModification: false);
                    break;
                case "$push":
                    ApplyPush(result, (BsonDocument)spec);
                    break;
                case "$pull":
                    ApplyPull(result, (BsonDocument)spec);
                    break;
                case "$pullAll":
                    ApplyPullAll(result, (BsonDocument)spec);
                    break;
                case "$pop":
                    ApplyPop(result, (BsonDocument)spec);
                    break;
                case "$addToSet":
                    ApplyAddToSet(result, (BsonDocument)spec);
                    break;
                case "$rename":
                    ApplyRename(result, (BsonDocument)spec);
                    break;
                case "$currentDate":
                    ApplyCurrentDate(result, (BsonDocument)spec);
                    break;
                case "$setOnInsert":
                    if (isUpsertInsert)
                        ApplySet(result, (BsonDocument)spec, allowIdModification: isUpsertInsert);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported update operator: {op}");
            }
        }

        return result;
    }

    private static void ApplySet(BsonDocument doc, BsonDocument spec, bool allowIdModification = false)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id" && !allowIdModification)
                throw new NotSupportedException("Cannot modify _id field");

            BsonPath.SetValueByPath(doc, element.Name, element.Value);
        }
    }

    private static void ApplyUnset(BsonDocument doc, BsonDocument spec, bool allowIdModification = false)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id" && !allowIdModification)
                throw new NotSupportedException("Cannot modify _id field");

            BsonPath.RemoveValueByPath(doc, element.Name);
        }
    }

    private static void ApplyInc(BsonDocument doc, BsonDocument spec, bool allowIdModification = false)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id" && !allowIdModification)
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

    private static void ApplyMul(BsonDocument doc, BsonDocument spec, bool allowIdModification = false)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id" && !allowIdModification)
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

    private static void ApplyMinMax(BsonDocument doc, BsonDocument spec, bool min, bool allowIdModification = false)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id" && !allowIdModification)
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            if (current == null || current.BsonType == BsonType.Null)
            {
                BsonPath.SetValueByPath(doc, element.Name, element.Value);
                continue;
            }

            int comparison = element.Value.CompareTo(current);
            bool shouldReplace = min ? comparison < 0 : comparison > 0;
            if (shouldReplace)
                BsonPath.SetValueByPath(doc, element.Name, element.Value);
        }
    }

    private static void ApplyPush(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var array = GetOrCreateArray(doc, element.Name);

            if (element.Value is BsonDocument modifiers && modifiers.Names.Any(n => n == "$each"))
            {
                ApplyPushModifiers(array, modifiers);
            }
            else
            {
                array.Add(element.Value);
            }
        }
    }

    private static void ApplyPushModifiers(BsonArray array, BsonDocument modifiers)
    {
        if (!modifiers.TryGetValue("$each", out var eachValue) || eachValue is not BsonArray each)
            throw new NotSupportedException("$each requires an array");

        int insertAt = array.Count;
        if (modifiers.TryGetValue("$position", out var positionValue))
        {
            int position = positionValue.ToInt32();
            insertAt = position < 0 ? Math.Max(0, array.Count + position) : Math.Min(array.Count, position);
        }

        for (int i = 0; i < each.Count; i++)
            array.Insert(insertAt + i, each[i]);

        if (modifiers.TryGetValue("$sort", out var sortValue))
            SortArrayInPlace(array, sortValue);

        if (modifiers.TryGetValue("$slice", out var sliceValue))
            SliceArrayInPlace(array, sliceValue.ToInt32());
    }

    private static void SortArrayInPlace(BsonArray array, BsonValue sortSpec)
    {
        List<BsonValue> sorted;

        if (sortSpec is BsonDocument fieldSpec)
        {
            var field = fieldSpec.Names.First();
            int direction = fieldSpec[field].ToInt32();
            sorted = array
                .OrderBy(v => v is BsonDocument d ? BsonPath.GetValue(d, field) ?? BsonNull.Value : BsonNull.Value,
                    Comparer<BsonValue>.Create((a, b) => direction * a.CompareTo(b)))
                .ToList();
        }
        else
        {
            int direction = sortSpec.ToInt32();
            sorted = array.OrderBy(v => v, Comparer<BsonValue>.Create((a, b) => direction * a.CompareTo(b))).ToList();
        }

        array.Clear();
        array.AddRange(sorted);
    }

    private static void SliceArrayInPlace(BsonArray array, int slice)
    {
        List<BsonValue> sliced = slice switch
        {
            0 => [],
            > 0 => array.Take(slice).ToList(),
            _ => array.Skip(Math.Max(0, array.Count + slice)).ToList()
        };

        array.Clear();
        array.AddRange(sliced);
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

    private static void ApplyPullAll(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            if (element.Value is not BsonArray valuesToRemove)
                throw new NotSupportedException("$pullAll requires an array");

            var current = BsonPath.GetValue(doc, element.Name);
            if (current is BsonArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (valuesToRemove.Any(v => array[i].CompareTo(v) == 0))
                        array.RemoveAt(i);
                }
            }
        }
    }

    private static void ApplyPop(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var current = BsonPath.GetValue(doc, element.Name);
            if (current is not BsonArray array || array.Count == 0)
                continue;

            int direction = element.Value.ToInt32();
            switch (direction)
            {
                case 1:
                    array.RemoveAt(array.Count - 1);
                    break;
                case -1:
                    array.RemoveAt(0);
                    break;
                default:
                    throw new NotSupportedException("$pop requires 1 or -1");
            }
        }
    }

    private static void ApplyAddToSet(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            var array = GetOrCreateArray(doc, element.Name);

            var valuesToAdd = element.Value is BsonDocument modifiers && modifiers.Names.Any(n => n == "$each")
                ? (BsonArray)modifiers["$each"]
                : new BsonArray { element.Value };

            foreach (var value in valuesToAdd)
            {
                if (!array.Any(v => v.CompareTo(value) == 0))
                    array.Add(value);
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

    private static void ApplyCurrentDate(BsonDocument doc, BsonDocument spec)
    {
        foreach (var element in spec.Elements)
        {
            if (element.Name == "_id")
                throw new NotSupportedException("Cannot modify _id field");

            bool isTimestamp = element.Value is BsonDocument typeSpec
                && typeSpec.TryGetValue("$type", out var typeValue)
                && typeValue.IsString
                && typeValue.AsString == "timestamp";

            BsonValue value = isTimestamp
                ? new BsonTimestamp((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1)
                : new BsonDateTime(DateTime.UtcNow);

            BsonPath.SetValueByPath(doc, element.Name, value);
        }
    }

    private static BsonArray GetOrCreateArray(BsonDocument doc, string field)
    {
        var current = BsonPath.GetValue(doc, field);

        if (current == null || current.BsonType == BsonType.Null)
        {
            var array = new BsonArray();
            BsonPath.SetValueByPath(doc, field, array);
            return array;
        }

        if (current.IsBsonArray)
            return (BsonArray)current;

        throw new NotSupportedException($"Cannot apply array update operator to non-array field '{field}'");
    }
}
