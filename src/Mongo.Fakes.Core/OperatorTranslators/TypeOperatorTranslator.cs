using System.Linq.Expressions;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/type/
// Ported from MongoZen's TypeFilterElementTranslator (a handful of CLR TypeIs checks) to
// the full BSON type-name table, including the "number" alias.
public sealed class TypeOperatorTranslator : IOperatorTranslator
{
    private static readonly BsonType[] NumberAlias =
    [
        BsonType.Int32, BsonType.Int64, BsonType.Double, BsonType.Decimal128
    ];

    private static readonly Dictionary<string, BsonType> TypesByName = new()
    {
        ["double"] = BsonType.Double,
        ["string"] = BsonType.String,
        ["object"] = BsonType.Document,
        ["array"] = BsonType.Array,
        ["binData"] = BsonType.Binary,
        ["undefined"] = BsonType.Undefined,
        ["objectId"] = BsonType.ObjectId,
        ["bool"] = BsonType.Boolean,
        ["date"] = BsonType.DateTime,
        ["null"] = BsonType.Null,
        ["regex"] = BsonType.RegularExpression,
        ["javascript"] = BsonType.JavaScript,
        ["int"] = BsonType.Int32,
        ["timestamp"] = BsonType.Timestamp,
        ["long"] = BsonType.Int64,
        ["decimal"] = BsonType.Decimal128,
    };

    public string Operator => "$type";

    public Expression Translate(Expression fieldValueExpr, BsonValue operatorValue)
    {
        var types = operatorValue switch
        {
            { IsString: true } => ResolveTypeNames([operatorValue.AsString]),
            { IsBsonArray: true } => ResolveTypeNames(operatorValue.AsBsonArray.Select(v => v.AsString)),
            _ => throw new ArgumentException("$type requires a string or array of strings", nameof(operatorValue)),
        };

        return Expression.Call(BsonMatcherMethods.CheckType, fieldValueExpr, Expression.Constant(types));
    }

    private static BsonType[] ResolveTypeNames(IEnumerable<string> names) =>
        names.SelectMany(ResolveTypeName).Distinct().ToArray();

    private static IEnumerable<BsonType> ResolveTypeName(string name)
    {
        if (name == "number")
        {
            return NumberAlias;
        }

        if (TypesByName.TryGetValue(name, out var type))
        {
            return [type];
        }

        throw new NotSupportedException($"BSON type '{name}' is not supported");
    }
}
