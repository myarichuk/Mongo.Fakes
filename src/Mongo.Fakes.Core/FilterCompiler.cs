using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using Mongo.Fakes.Core.OperatorTranslators;

namespace Mongo.Fakes.Core;

/// <summary>
/// Compiles MongoDB filter documents into <see cref="Expression{TDelegate}"/> predicates
/// over <see cref="BsonDocument"/>, using BsonValue-native comparisons throughout so
/// null/missing/array/type-ordering semantics match real MongoDB. See docs/SPEC.md.
/// </summary>
public sealed class FilterCompiler
{
    private static readonly MethodInfo GetFieldValueMethod =
        typeof(BsonPath).GetMethod(nameof(BsonPath.GetValue)) ?? throw new MissingMethodException(nameof(BsonPath), nameof(BsonPath.GetValue));

    private readonly Dictionary<string, IOperatorTranslator> _operators;

    public FilterCompiler()
    {
        IOperatorTranslator[] translators =
        [
            new EqOperatorTranslator(),
            new NeOperatorTranslator(),
            new GtOperatorTranslator(),
            new GteOperatorTranslator(),
            new LtOperatorTranslator(),
            new LteOperatorTranslator(),
            new InOperatorTranslator(),
            new NinOperatorTranslator(),
            new ExistsOperatorTranslator(),
            new TypeOperatorTranslator(),
            new RegexOperatorTranslator(),
            new AllOperatorTranslator(),
            new ElemMatchOperatorTranslator(this),
        ];

        _operators = translators.ToDictionary(t => t.Operator);
    }

    /// <summary>Compile a MongoDB filter document to an executable predicate.</summary>
    public Func<BsonDocument, bool> Compile(BsonDocument filter) => CompileExpression(filter).Compile();

    /// <summary>Compile to an expression tree (for composition/debugging).</summary>
    public Expression<Func<BsonDocument, bool>> CompileExpression(BsonDocument filter)
    {
        var docParam = Expression.Parameter(typeof(BsonDocument), "doc");
        var body = CompileFilterBody(filter, docParam);
        return Expression.Lambda<Func<BsonDocument, bool>>(body, docParam);
    }

    /// <summary>
    /// Compiles a condition document for use as an <c>$elemMatch</c> element predicate.
    /// Document-shaped conditions (field names) recurse as a nested BsonDocument filter;
    /// operator-shaped conditions (all keys start with "$") apply directly to the element.
    /// </summary>
    internal Func<BsonValue, bool> CompileElementPredicate(BsonDocument condition)
    {
        if (IsOperatorDocument(condition))
        {
            var elementParam = Expression.Parameter(typeof(BsonValue), "elem");
            var body = CompileOperatorClauses(condition, elementParam, elementParam);
            return Expression.Lambda<Func<BsonValue, bool>>(body, elementParam).Compile();
        }

        var nestedPredicate = Compile(condition);
        return value => value is BsonDocument doc && nestedPredicate(doc);
    }

    private Expression CompileFilterBody(BsonDocument filter, ParameterExpression docParam)
    {
        Expression? combined = null;

        foreach (var element in filter.Elements)
        {
            var clause = element.Name switch
            {
                "$and" => CompileLogicalArray(element.Value, docParam, Expression.AndAlso),
                "$or" => CompileLogicalArray(element.Value, docParam, Expression.OrElse),
                _ when element.Name.StartsWith('$') =>
                    throw new NotSupportedException($"Unsupported top-level operator '{element.Name}'"),
                _ => CompileFieldCondition(element.Name, element.Value, docParam),
            };

            combined = combined is null ? clause : Expression.AndAlso(combined, clause);
        }

        return combined ?? Expression.Constant(true);
    }

    private Expression CompileLogicalArray(BsonValue value, ParameterExpression docParam, Func<Expression, Expression, Expression> combine)
    {
        if (value is not BsonArray array || array.Count == 0)
        {
            throw new ArgumentException("$and/$or requires a non-empty array of filter documents");
        }

        return array
            .Select(d => CompileFilterBody((BsonDocument)d, docParam))
            .Aggregate(combine);
    }

    private Expression CompileFieldCondition(string field, BsonValue condition, ParameterExpression docParam)
    {
        var fieldValueExpr = Expression.Call(GetFieldValueMethod, docParam, Expression.Constant(field));

        // Extended JSON parses `{ $regex: "...", $options: "..." }` directly into a
        // BsonRegularExpression value (rather than leaving it as a BsonDocument), the same
        // shape the driver produces for `{ field: /pattern/opts }` filters.
        if (condition.IsBsonRegularExpression)
        {
            return CompileOperator("$regex", fieldValueExpr, condition, docParam);
        }

        return IsOperatorDocument(condition)
            ? CompileOperatorClauses((BsonDocument)condition, fieldValueExpr, docParam)
            : CompileOperator("$eq", fieldValueExpr, condition, docParam);
    }

    /// <summary>ANDs together every operator in a <c>{ $op1: ..., $op2: ... }</c> document, against a shared value expression.</summary>
    private Expression CompileOperatorClauses(BsonDocument condDoc, Expression valueExpr, ParameterExpression docParam)
    {
        var options = condDoc.TryGetValue("$options", out var opt) ? opt.AsString : null;
        Expression? combined = null;

        foreach (var op in condDoc.Elements)
        {
            if (op.Name == "$options")
            {
                continue; // consumed alongside $regex below
            }

            var opValue = op.Name == "$regex" && options is not null
                ? new BsonDocument { { "$regex", op.Value }, { "$options", options } }
                : op.Value;

            var clause = CompileOperator(op.Name, valueExpr, opValue, docParam);
            combined = combined is null ? clause : Expression.AndAlso(combined, clause);
        }

        return combined ?? throw new ArgumentException("Empty operator document in filter");
    }

    private Expression CompileOperator(string op, Expression valueExpr, BsonValue operatorValue, ParameterExpression docParam)
    {
        if (op == "$not")
        {
            if (operatorValue is not BsonDocument notCondition)
            {
                throw new ArgumentException("$not requires a document", nameof(operatorValue));
            }

            return Expression.Not(CompileOperatorClauses(notCondition, valueExpr, docParam));
        }

        if (!_operators.TryGetValue(op, out var translator))
        {
            throw new NotSupportedException($"Unsupported operator '{op}'");
        }

        return translator.Translate(valueExpr, operatorValue);
    }

    private static bool IsOperatorDocument(BsonValue value) =>
        value is BsonDocument doc && doc.ElementCount > 0 && doc.Names.All(n => n.StartsWith('$'));
}
