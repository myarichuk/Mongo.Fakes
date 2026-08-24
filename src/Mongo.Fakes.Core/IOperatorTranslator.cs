using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core;

/// <summary>
/// Translates a single MongoDB query operator into an expression testing a field's
/// <see cref="BsonValue"/> against the operator's value from the filter document.
/// </summary>
public interface IOperatorTranslator
{
    /// <summary>The operator this translator handles, e.g. "$eq", "$in".</summary>
    string Operator { get; }

    /// <param name="fieldValueExpr">Expression of type <see cref="BsonValue"/> (nullable) — the extracted field value.</param>
    /// <param name="operatorValue">The value from the filter, e.g. <c>5</c> for <c>{ $gt: 5 }</c>.</param>
    Expression Translate(Expression fieldValueExpr, BsonValue operatorValue);
}
