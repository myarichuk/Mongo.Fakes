using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core;

/// <summary>
/// Pure BsonValue-native matching logic behind every operator translator. Kept as plain
/// static methods (rather than inlined expression trees) so <see cref="FilterCompiler"/>
/// only needs one <c>Expression.Call</c> per operator, and so the semantics here are
/// directly unit-testable without going through expression compilation.
/// </summary>
public static class BsonMatchers
{
    private static bool MatchesScalarOrArray(BsonValue field, Func<BsonValue, bool> predicate) =>
        field is BsonArray array ? array.Any(predicate) || predicate(field) : predicate(field);

    private static bool SameTypeBracket(BsonValue a, BsonValue b)
    {
        var aType = a.BsonType;
        var bType = b.BsonType;

        if (aType == bType)
            return true;

        return a.IsNumeric && b.IsNumeric;
    }

    /// <summary>
    /// <c>{ field: value }</c> semantics: a missing field matches only when <paramref name="expected"/>
    /// is BSON null (mirrors MongoDB treating missing as null for equality purposes).
    /// </summary>
    public static bool Eq(BsonValue? field, BsonValue expected) =>
        field is null
            ? expected.BsonType == BsonType.Null
            : MatchesScalarOrArray(field, v => v.CompareTo(expected) == 0);

    public static bool Ne(BsonValue? field, BsonValue expected) => !Eq(field, expected);

    public static bool Gt(BsonValue? field, BsonValue expected) =>
        field is not null && MatchesScalarOrArray(field, v => SameTypeBracket(v, expected) && v.CompareTo(expected) > 0);

    public static bool Gte(BsonValue? field, BsonValue expected) =>
        field is not null && MatchesScalarOrArray(field, v => SameTypeBracket(v, expected) && v.CompareTo(expected) >= 0);

    public static bool Lt(BsonValue? field, BsonValue expected) =>
        field is not null && MatchesScalarOrArray(field, v => SameTypeBracket(v, expected) && v.CompareTo(expected) < 0);

    public static bool Lte(BsonValue? field, BsonValue expected) =>
        field is not null && MatchesScalarOrArray(field, v => SameTypeBracket(v, expected) && v.CompareTo(expected) <= 0);

    public static bool In(BsonValue? field, BsonArray values)
    {
        if (field is not null)
            return MatchesScalarOrArray(field, v => values.Any(x => v.CompareTo(x) == 0));

        return values.Any(x => x.BsonType == BsonType.Null);
    }

    public static bool Nin(BsonValue? field, BsonArray values) => !In(field, values);

    public static bool Exists(BsonValue? field, bool expected) => (field is not null) == expected;

    public static bool CheckType(BsonValue? field, BsonType[] expectedTypes) =>
        field is not null && MatchesScalarOrArray(field, v => expectedTypes.Contains(v.BsonType));

    public static bool RegexMatch(BsonValue? field, string pattern, RegexOptions options) =>
        field is not null && MatchesScalarOrArray(field, v => v.IsString && Regex.IsMatch(v.AsString, pattern, options));

    /// <summary>
    /// <c>$all</c>: every value in <paramref name="values"/> must be present in the field's array.
    /// A non-array field is treated as a single-element array for this purpose.
    /// </summary>
    public static bool All(BsonValue? field, BsonArray values)
    {
        if (field is null)
        {
            return false;
        }

        return field is BsonArray array
            ? values.All(v => array.Any(item => item.CompareTo(v) == 0))
            : values.All(v => field.CompareTo(v) == 0);
    }

    public static bool ElemMatch(BsonValue? field, Func<BsonValue, bool> elementPredicate) =>
        field is BsonArray array && array.Any(elementPredicate);
}
