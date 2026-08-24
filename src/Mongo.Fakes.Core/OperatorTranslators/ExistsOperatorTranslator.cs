using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/exists/
// Note: MongoZen's ExistsFilterElementTranslator throws NotSupportedException, since it
// operates over strongly-typed CLR models where "field doesn't exist" isn't meaningful.
// Mongo.Fakes.Core is BsonDocument-native, where that distinction is exactly the point.
public sealed class ExistsOperatorTranslator : IOperatorTranslator
{
    public string Operator => "$exists";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        if (!operatorValue.IsBoolean)
        {
            throw new ArgumentException("$exists requires a boolean value", nameof(operatorValue));
        }

        return Expression.Call(BsonMatcherMethods.Exists, fieldValueExpr, Expression.Constant(operatorValue.AsBoolean));
    }
}
