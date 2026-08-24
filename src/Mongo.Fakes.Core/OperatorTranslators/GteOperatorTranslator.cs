namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/gte/
public sealed class GteOperatorTranslator() : BinaryOperatorTranslatorBase(BsonMatcherMethods.Gte)
{
    public override string Operator => "$gte";
}
