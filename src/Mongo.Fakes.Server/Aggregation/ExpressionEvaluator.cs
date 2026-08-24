using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class ExpressionEvaluator
{
    public static BsonValue Evaluate(BsonValue expr, BsonDocument doc)
    {
        if (expr == null)
            return BsonNull.Value;

        if (expr.IsString)
        {
            string str = expr.AsString;
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
                    return EvaluateOperator(firstElem.Name, firstElem.Value, doc);
            }
            return expr;
        }

        return expr;
    }

    private static BsonValue EvaluateOperator(string op, BsonValue args, BsonDocument doc)
    {
        return op switch
        {
            "$concat" => EvalConcat(args, doc),
            "$toUpper" => EvalToUpper(args, doc),
            "$toLower" => EvalToLower(args, doc),
            "$multiply" => EvalMultiply(args, doc),
            "$add" => EvalAdd(args, doc),
            "$subtract" => EvalSubtract(args, doc),
            "$divide" => EvalDivide(args, doc),
            "$cond" => EvalCond(args, doc),
            "$ifNull" => EvalIfNull(args, doc),
            _ => throw new NotSupportedException($"Operator {op} not supported in expression context.")
        };
    }

    private static BsonValue EvalConcat(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array)
            throw new ArgumentException("$concat requires an array of strings");

        var parts = new List<string>();
        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc);
            if (val.BsonType == BsonType.Null)
                return BsonNull.Value;
            var str = val.ToString();
            if (str != null)
                parts.Add(str);
        }

        return new BsonString(string.Concat(parts));
    }

    private static BsonValue EvalToUpper(BsonValue args, BsonDocument doc)
    {
        var val = Evaluate(args, doc);
        if (!val.IsString)
            throw new ArgumentException("$toUpper requires a string");
        return new BsonString(val.AsString.ToUpperInvariant());
    }

    private static BsonValue EvalToLower(BsonValue args, BsonDocument doc)
    {
        var val = Evaluate(args, doc);
        if (!val.IsString)
            throw new ArgumentException("$toLower requires a string");
        return new BsonString(val.AsString.ToLowerInvariant());
    }

    private static BsonValue EvalMultiply(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array || array.Count < 2)
            throw new ArgumentException("$multiply requires an array of at least 2 numbers");

        double product = 1.0;
        bool isDouble = false;

        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc);
            if (!val.IsNumeric)
                throw new ArgumentException("$multiply requires numeric arguments");

            product *= val.ToDouble();
            if (val.IsDouble)
                isDouble = true;
        }

        return isDouble ? new BsonDouble(product) : new BsonInt64((long)product);
    }

    private static BsonValue EvalAdd(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array)
            throw new ArgumentException("$add requires an array");

        double sum = 0;
        bool isDouble = false;

        foreach (var elem in array)
        {
            var val = Evaluate(elem, doc);
            if (val.IsNumeric)
            {
                sum += val.ToDouble();
                if (val.IsDouble)
                    isDouble = true;
            }
            else if (val.IsString && array.Count == 2)
            {
                var other = Evaluate(array[array.IndexOf(elem) == 0 ? 1 : 0], doc);
                return new BsonString(val.AsString + (other.IsString ? other.AsString : other.ToString()));
            }
        }

        return isDouble ? new BsonDouble(sum) : new BsonInt64((long)sum);
    }

    private static BsonValue EvalSubtract(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$subtract requires exactly 2 numbers");

        var first = Evaluate(array[0], doc);
        var second = Evaluate(array[1], doc);

        if (!first.IsNumeric || !second.IsNumeric)
            throw new ArgumentException("$subtract requires numeric arguments");

        var diff = first.ToDouble() - second.ToDouble();
        return first.IsDouble || second.IsDouble ? new BsonDouble(diff) : new BsonInt64((long)diff);
    }

    private static BsonValue EvalDivide(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$divide requires exactly 2 numbers");

        var first = Evaluate(array[0], doc);
        var second = Evaluate(array[1], doc);

        if (!first.IsNumeric || !second.IsNumeric)
            throw new ArgumentException("$divide requires numeric arguments");

        if (second.ToDouble() == 0)
            throw new DivideByZeroException("Cannot divide by zero");

        return new BsonDouble(first.ToDouble() / second.ToDouble());
    }

    private static BsonValue EvalCond(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array || array.Count != 3)
            throw new ArgumentException("$cond requires 3 arguments: [condition, trueValue, falseValue]");

        var condition = Evaluate(array[0], doc);
        var isTruthy = condition.BsonType != BsonType.Null && condition.BsonType != BsonType.Boolean
            ? true
            : condition.BsonType == BsonType.Boolean ? condition.AsBoolean : false;

        return isTruthy ? Evaluate(array[1], doc) : Evaluate(array[2], doc);
    }

    private static BsonValue EvalIfNull(BsonValue args, BsonDocument doc)
    {
        if (args is not BsonArray array || array.Count != 2)
            throw new ArgumentException("$ifNull requires 2 arguments: [value, fallback]");

        var value = Evaluate(array[0], doc);
        return value.BsonType == BsonType.Null ? Evaluate(array[1], doc) : value;
    }
}
