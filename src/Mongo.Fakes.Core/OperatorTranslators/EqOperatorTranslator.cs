namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/eq/
public sealed class EqOperatorTranslator() : BinaryOperatorTranslatorBase(BsonMatcherMethods.Eq)
{
    public override string Operator => "$eq";
}
