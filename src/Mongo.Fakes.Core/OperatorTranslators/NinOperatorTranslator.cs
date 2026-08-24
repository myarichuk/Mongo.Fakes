using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/nin/
public sealed class NinOperatorTranslator : IOperatorTranslator
{
    public string Operator => "$nin";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        if (operatorValue is not BsonArray values)
        {
            throw new ArgumentException("$nin requires an array of values", nameof(operatorValue));
        }

        return Expression.Call(BsonMatcherMethods.Nin, fieldValueExpr, Expression.Constant(values));
    }
}
