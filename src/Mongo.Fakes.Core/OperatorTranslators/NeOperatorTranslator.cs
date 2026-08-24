namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/ne/
public sealed class NeOperatorTranslator() : BinaryOperatorTranslatorBase(BsonMatcherMethods.Ne)
{
    public override string Operator => "$ne";
}
