namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/lt/
public sealed class LtOperatorTranslator() : BinaryOperatorTranslatorBase(BsonMatcherMethods.Lt)
{
    public override string Operator => "$lt";
}
