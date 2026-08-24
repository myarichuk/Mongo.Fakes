namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/lte/
public sealed class LteOperatorTranslator() : BinaryOperatorTranslatorBase(BsonMatcherMethods.Lte)
{
    public override string Operator => "$lte";
}
