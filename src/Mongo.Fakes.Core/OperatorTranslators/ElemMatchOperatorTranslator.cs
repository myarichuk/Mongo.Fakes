using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/elemMatch/
// Ported from MongoZen's ElemMatchFilterElementTranslator, which recursed into a
// FilterToLinqTranslatorFactory for the array's CLR element type. Mongo.Fakes.Core has one
// element shape (BsonValue) instead of many CLR types, so it recurses into the owning
// FilterCompiler directly: document-shaped conditions recompile as a nested filter, and
// operator-shaped conditions ($eq, $gt, ...) apply directly to each array element.
public sealed class ElemMatchOperatorTranslator(FilterCompiler compiler) : IOperatorTranslator
{
    public string Operator => "$elemMatch";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        if (operatorValue is not BsonDocument condition)
        {
            throw new ArgumentException("$elemMatch requires a document", nameof(operatorValue));
        }

        var elementPredicate = compiler.CompileElementPredicate(condition);
        return Expression.Call(BsonMatcherMethods.ElemMatch, fieldValueExpr, Expression.Constant(elementPredicate));
    }
}
