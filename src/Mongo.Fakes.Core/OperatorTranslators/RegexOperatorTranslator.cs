using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/regex/
// Ported from MongoZen's FilterRegexElementTranslator: pattern/options parsing (including
// the i/m/s/x flag mapping) is unchanged; expression-building targets BsonMatchers.RegexMatch
// instead of Regex.IsMatch over a reflected CLR string member.
// FilterCompiler folds a sibling "$options" element into this operator's value as
// { $regex: pattern, $options: "..." } before calling Translate, so both forms below are handled.
public sealed class RegexOperatorTranslator : IOperatorTranslator
{
    public string Operator => "$regex";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        var (pattern, optionsText) = ParsePatternAndOptions(operatorValue);
        var options = ParseOptions(optionsText);

        return Expression.Call(
            BsonMatcherMethods.RegexMatch,
            fieldValueExpr,
            Expression.Constant(pattern),
            Expression.Constant(options));
    }

    private static (string Pattern, string? Options) ParsePatternAndOptions(BsonValue value) => value switch
    {
        { IsBsonRegularExpression: true } => (value.AsBsonRegularExpression.Pattern, value.AsBsonRegularExpression.Options),
        { IsString: true } => (value.AsString, null),
        { IsBsonDocument: true } => ParseFromDocument(value.AsBsonDocument),
        _ => throw new ArgumentException("Invalid value for $regex operator", nameof(value)),
    };

    private static (string, string?) ParseFromDocument(BsonDocument doc)
    {
        var pattern = doc["$regex"].AsString;
        var options = doc.TryGetValue("$options", out var opts) ? opts.AsString : null;
        return (pattern, options);
    }

    private static RegexOptions ParseOptions(string? optionsText)
    {
        var options = RegexOptions.None;
        if (optionsText is null)
        {
            return options;
        }

        foreach (var ch in optionsText)
        {
            options |= ch switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => throw new NotSupportedException($"Regex option '{ch}' is not supported."),
            };
        }

        return options;
    }
}
