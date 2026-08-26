using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Errors;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class ExpressionEvaluator
{
    public static BsonValue Evaluate(BsonValue expr, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (expr == null)
            return BsonNull.Value;

        if (expr.IsString)
        {
            string str = expr.AsString;
            if (str.StartsWith("$$"))
            {
                string varName = str[2..];

                if (varName == "ROOT" || varName == "CURRENT")
                    return doc;

                int dotIndex = varName.IndexOf('.');
                string baseVar = dotIndex >= 0 ? varName[..dotIndex] : varName;
                string subPath = dotIndex >= 0 ? varName[(dotIndex + 1)..] : "";

                if (variables == null || !variables.TryGetValue(baseVar, out var varValue))
                    throw new MongoCommandException(ErrorCodes.BadValue, "InvalidVariable", $"Use of undefined variable: $${baseVar}");

                if (!string.IsNullOrEmpty(subPath) && varValue.IsBsonDocument)
                    return BsonPath.GetValue((BsonDocument)varValue, subPath) ?? BsonNull.Value;

                return varValue;
            }

            if (str.StartsWith("$"))
                return BsonPath.GetValue(doc, str[1..]) ?? BsonNull.Value;
            return BsonString.Create(str);
        }

        if (expr.IsInt32 || expr.IsInt64 || expr.IsDouble || expr.IsDecimal128)
            return expr;

        if (expr.IsBoolean)
            return expr;

        if (expr.BsonType == BsonType.Null)
            return BsonNull.Value;

        if (expr.IsBsonDocument)
        {
            var doc_expr = (BsonDocument)expr;
            if (doc_expr.ElementCount == 1)
            {
                var firstElem = doc_expr.GetElement(0);
                if (firstElem.Name.StartsWith("$"))
                    return EvaluateOperator(firstElem.Name, firstElem.Value, doc, variables);
            }
            return expr;
        }

        return expr;
    }

    private static BsonValue EvaluateOperator(string op, BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        return op switch
        {
            "$concat" => EvalConcat(args, doc, variables),
            "$toUpper" => EvalToUpper(args, doc, variables),
            "$toLower" => EvalToLower(args, doc, variables),
            "$multiply" => EvalMultiply(args, doc, variables),
            "$add" => EvalAdd(args, doc, variables),
            "$subtract" => EvalSubtract(args, doc, variables),
            "$divide" => EvalDivide(args, doc, variables),
            "$cond" => EvalCond(args, doc, variables),
            "$ifNull" => EvalIfNull(args, doc, variables),
            "$arrayElemAt" => EvalArrayElemAt(args, doc, variables),
            "$eq" => EvalEq(args, doc, variables),
            "$ne" => EvalNe(args, doc, variables),
            "$gt" => EvalGt(args, doc, variables),
            "$gte" => EvalGte(args, doc, variables),
            "$lt" => EvalLt(args, doc, variables),
            "$lte" => EvalLte(args, doc, variables),
            "$and" => EvalAnd(args, doc, variables),
            "$or" => EvalOr(args, doc, variables),
            "$not" => EvalNot(args, doc, variables),
            "$meta" => EvalMeta(args, doc),
            _ => throw new NotSupportedException($"Operator {op} not supported in expression context.")
        };
    }

    private static bool IsTruthy(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Null => false,
            BsonType.Boolean => value.AsBoolean,
            BsonType.Int32 => value.AsInt32 != 0,
            BsonType.Int64 => value.AsInt64 != 0,
            BsonType.Double => value.AsDouble != 0.0d,
            BsonType.Decimal128 => value.AsDecimal128 != 0m,
            _ => true
        };
    }

    private static BsonValue EvalConcat(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array)
            throw new ArgumentException("$concat requires an array of strings");

        var parts = new List<string>();
        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc, variables);
            if (val.BsonType == BsonType.Null)
                return BsonNull.Value;
            var str = val.ToString();
            if (str != null)
                parts.Add(str);
        }

        return new BsonString(string.Concat(parts));
    }

    private static BsonValue EvalToUpper(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        var val = Evaluate(args, doc, variables);
        if (!val.IsString)
            throw new ArgumentException("$toUpper requires a string");
        return new BsonString(val.AsString.ToUpperInvariant());
    }

    private static BsonValue EvalToLower(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        var val = Evaluate(args, doc, variables);
        if (!val.IsString)
            throw new ArgumentException("$toLower requires a string");
        return new BsonString(val.AsString.ToLowerInvariant());
    }

    private static BsonValue EvalMultiply(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count < 2)
            throw new ArgumentException("$multiply requires an array of at least 2 numbers");

        double product = 1.0;
        bool isDouble = false;

        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc, variables);
            if (!val.IsNumeric)
                throw new ArgumentException("$multiply requires numeric arguments");

            product *= val.ToDouble();
            if (val.IsDouble)
                isDouble = true;
        }

        return isDouble ? new BsonDouble(product) : new BsonInt64((long)product);
    }

    private static BsonValue EvalAdd(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array)
            throw new ArgumentException("$add requires an array");

        double sum = 0;
        bool isDouble = false;

        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc, variables);
            if (val.IsNumeric)
            {
                sum += val.ToDouble();
                if (val.IsDouble)
                    isDouble = true;
            }
            else if (val.IsString && array.Count == 2)
            {
                var other = Evaluate(array[array.IndexOf(elem) == 0 ? 1 : 0], doc, variables);
                return new BsonString(val.AsString + (other.IsString ? other.AsString : other.ToString()));
            }
        }

        return isDouble ? new BsonDouble(sum) : new BsonInt64((long)sum);
    }

    private static BsonValue EvalSubtract(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$subtract requires exactly 2 numbers");

        var first = Evaluate(array[0], doc, variables);
        var second = Evaluate(array[1], doc, variables);

        if (!first.IsNumeric || !second.IsNumeric)
            throw new ArgumentException("$subtract requires numeric arguments");

        var diff = first.ToDouble() - second.ToDouble();
        return first.IsDouble || second.IsDouble ? new BsonDouble(diff) : new BsonInt64((long)diff);
    }

    private static BsonValue EvalDivide(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$divide requires exactly 2 numbers");

        var first = Evaluate(array[0], doc, variables);
        var second = Evaluate(array[1], doc, variables);

        if (!first.IsNumeric || !second.IsNumeric)
            throw new ArgumentException("$divide requires numeric arguments");

        if (second.ToDouble() == 0)
            throw new DivideByZeroException("Cannot divide by zero");

        return new BsonDouble(first.ToDouble() / second.ToDouble());
    }

    private static BsonValue EvalCond(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 3)
            throw new ArgumentException("$cond requires 3 arguments: [condition, trueValue, falseValue]");

        var condition = Evaluate(array[0], doc, variables);
        var isTruthy = IsTruthy(condition);

        return isTruthy ? Evaluate(array[1], doc, variables) : Evaluate(array[2], doc, variables);
    }

    private static BsonValue EvalIfNull(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$ifNull requires 2 arguments: [value, fallback]");

        var value = Evaluate(array[0], doc, variables);
        return value.BsonType == BsonType.Null ? Evaluate(array[1], doc, variables) : value;
    }

    private static BsonValue EvalArrayElemAt(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$arrayElemAt requires 2 arguments: [array, index]");

        var arrayVal = Evaluate(array[0], doc, variables);
        var indexVal = Evaluate(array[1], doc, variables);

        if (arrayVal.BsonType == BsonType.Null || indexVal.BsonType == BsonType.Null)
            return BsonNull.Value;

        if (arrayVal is not BsonArray bsonArray)
            return BsonNull.Value;

        if (!indexVal.IsNumeric)
            return BsonNull.Value;

        int index = indexVal.ToInt32();
        if (index < 0)
            index += bsonArray.Count;

        if (index < 0 || index >= bsonArray.Count)
            return BsonNull.Value;

        return bsonArray[index];
    }

    private static BsonValue EvalEq(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$eq requires exactly 2 arguments");

        var left = Evaluate(array[0], doc, variables);
        var right = Evaluate(array[1], doc, variables);

        return left.CompareTo(right) == 0 ? BsonBoolean.True : BsonBoolean.False;
    }

    private static BsonValue EvalNe(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$ne requires exactly 2 arguments");

        var left = Evaluate(array[0], doc, variables);
        var right = Evaluate(array[1], doc, variables);

        return left.CompareTo(right) != 0 ? BsonBoolean.True : BsonBoolean.False;
    }

    private static BsonValue EvalGt(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$gt requires exactly 2 arguments");

        var left = Evaluate(array[0], doc, variables);
        var right = Evaluate(array[1], doc, variables);

        return left.CompareTo(right) > 0 ? BsonBoolean.True : BsonBoolean.False;
    }

    private static BsonValue EvalGte(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$gte requires exactly 2 arguments");

        var left = Evaluate(array[0], doc, variables);
        var right = Evaluate(array[1], doc, variables);

        return left.CompareTo(right) >= 0 ? BsonBoolean.True : BsonBoolean.False;
    }

    private static BsonValue EvalLt(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$lt requires exactly 2 arguments");

        var left = Evaluate(array[0], doc, variables);
        var right = Evaluate(array[1], doc, variables);

        return left.CompareTo(right) < 0 ? BsonBoolean.True : BsonBoolean.False;
    }

    private static BsonValue EvalLte(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$lte requires exactly 2 arguments");

        var left = Evaluate(array[0], doc, variables);
        var right = Evaluate(array[1], doc, variables);

        return left.CompareTo(right) <= 0 ? BsonBoolean.True : BsonBoolean.False;
    }

    private static BsonValue EvalAnd(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array)
            throw new ArgumentException("$and requires an array");

        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc, variables);
            if (!IsTruthy(val))
                return BsonBoolean.False;
        }

        return BsonBoolean.True;
    }

    private static BsonValue EvalOr(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        if (args is not BsonArray array)
            throw new ArgumentException("$or requires an array");

        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc, variables);
            if (IsTruthy(val))
                return BsonBoolean.True;
        }

        return BsonBoolean.False;
    }

    private static BsonValue EvalNot(BsonValue args, BsonDocument doc, IReadOnlyDictionary<string, BsonValue>? variables = null)
    {
        var val = Evaluate(args, doc, variables);
        return IsTruthy(val) ? BsonBoolean.False : BsonBoolean.True;
    }

    private static BsonValue EvalMeta(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonString metaArg)
            return BsonNull.Value;

        return metaArg.Value switch
        {
            "textScore" => EvalMetaTextScore(doc),
            "searchScore" => throw new NotSupportedException("$meta: \"searchScore\" requires $search queries, which are not yet supported"),
            "vectorSearchScore" => throw new NotSupportedException("$meta: \"vectorSearchScore\" requires $vectorSearch queries, which are not yet supported"),
            "indexKey" => BsonNull.Value, // Returns nothing if not available, doesn't error
            _ => throw new NotSupportedException($"$meta: \"{metaArg.Value}\" is not supported")
        };
    }

    private static BsonValue EvalMetaTextScore(BsonDocument doc)
    {
        if (doc.TryGetValue(Query.TextSearchFilter.ScoreField, out var score) && score.IsDouble)
            return score;
        throw new InvalidOperationException("$meta: \"textScore\" can only be used with $text queries");
    }
}
