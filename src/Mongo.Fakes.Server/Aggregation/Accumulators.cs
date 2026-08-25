using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

/// <summary>
/// Static accumulator functions for aggregation stages ($group, $setWindowFields, etc).
/// </summary>
internal static class Accumulators
{
    /// <summary>
    /// Computes the sum of a numeric expression across documents.
    /// </summary>
    public static BsonValue Sum(BsonValue expr, IReadOnlyList<BsonDocument> docs)
    {
        long intTotal = 0;
        double doubleTotal = 0;
        bool isDouble = false;
        bool overflowedInt64 = false;

        foreach (var doc in docs)
        {
            var value = ExpressionEvaluator.Evaluate(expr, doc);
            if (!value.IsNumeric)
                continue;

            if (!isDouble && (value.IsInt32 || value.IsInt64))
            {
                long asLong = value.ToInt64();
                try
                {
                    intTotal = checked(intTotal + asLong);
                }
                catch (OverflowException)
                {
                    overflowedInt64 = true;
                }
            }
            else
            {
                isDouble = true;
            }

            doubleTotal += value.ToDouble();
        }

        if (isDouble || overflowedInt64)
            return new BsonDouble(doubleTotal);

        return intTotal is >= int.MinValue and <= int.MaxValue
            ? new BsonInt32((int)intTotal)
            : new BsonInt64(intTotal);
    }

    /// <summary>
    /// Computes the average of a numeric expression across documents.
    /// </summary>
    public static BsonValue Avg(BsonValue expr, IReadOnlyList<BsonDocument> docs)
    {
        if (docs.Count == 0)
            return BsonNull.Value;

        double total = 0;
        int count = 0;
        foreach (var doc in docs)
        {
            var value = ExpressionEvaluator.Evaluate(expr, doc);
            if (value.IsNumeric)
            {
                total += value.ToDouble();
                count++;
            }
        }
        return count == 0 ? BsonNull.Value : new BsonDouble(total / count);
    }

    /// <summary>
    /// Computes the minimum or maximum of an expression across documents.
    /// </summary>
    public static BsonValue MinMax(BsonValue expr, IReadOnlyList<BsonDocument> docs, bool min)
    {
        BsonValue? result = null;
        foreach (var doc in docs)
        {
            var value = ExpressionEvaluator.Evaluate(expr, doc);
            if (value.BsonType == BsonType.Null)
                continue;

            if (result == null || (min ? value.CompareTo(result) < 0 : value.CompareTo(result) > 0))
                result = value;
        }
        return result ?? BsonNull.Value;
    }
}
