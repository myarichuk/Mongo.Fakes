using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/in/
public sealed class InOperatorTranslator : IOperatorTranslator
{
    public string Operator => "$in";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        if (operatorValue is not BsonArray values)
        {
            throw new ArgumentException("$in requires an array of values", nameof(operatorValue));
        }

        return Expression.Call(BsonMatcherMethods.In, fieldValueExpr, Expression.Constant(values));
    }
}
