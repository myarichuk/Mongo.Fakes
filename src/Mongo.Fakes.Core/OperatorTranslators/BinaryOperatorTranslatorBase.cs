using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

/// <summary>
/// Base for operators shaped as <c>BsonMatchers.Xxx(BsonValue? field, BsonValue expected)</c>.
/// Adapted from MongoZen's <c>BinaryOperatorFilterElementTranslator</c>, which built the
/// same shape over reflected CLR members instead of a BsonValue field-access expression.
/// </summary>
public abstract class BinaryOperatorTranslatorBase(MethodInfo matcherMethod) : IOperatorTranslator
{
    public abstract string Operator { get; }

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue) =>
        Expression.Call(matcherMethod, fieldValueExpr, Expression.Constant(operatorValue, typeof(BsonValue)));
}
