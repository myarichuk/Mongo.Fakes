namespace Mongo.Fakes.Core.OperatorTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/gt/
public sealed class GtOperatorTranslator() : BinaryOperatorTranslatorBase(BsonMatcherMethods.Gt)
{
    public override string Operator => "$gt";
}
