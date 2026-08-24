using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/all/
// Ported from MongoZen's AllOperatorFilterElementTranslator (reflection-based CLR array
// containment) to BsonValue-native array containment via BsonMatchers.All.
public sealed class AllOperatorTranslator : IOperatorTranslator
{
    public string Operator => "$all";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        if (operatorValue is not BsonArray values)
        {
            throw new ArgumentException("$all requires an array of values", nameof(operatorValue));
        }

        return Expression.Call(BsonMatcherMethods.All, fieldValueExpr, Expression.Constant(values));
    }
}
