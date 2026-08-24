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
                    throw new NotSupportedException($"Operator {firstElem.Name} not supported in expression context.");
            }
            return expr;
        }

        return expr;
    }
}
