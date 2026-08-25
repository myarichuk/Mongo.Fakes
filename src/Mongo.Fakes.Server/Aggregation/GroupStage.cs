using MongoDB.Bson;
using Mongo.Fakes.Core;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class GroupStage
{
    private readonly BsonValue _idSpec;
    private readonly Dictionary<string, BsonValue> _accumulators;

    public GroupStage(BsonDocument stageDoc)
    {
        if (!stageDoc.TryGetValue("_id", out _idSpec))
            throw new InvalidOperationException("$group requires _id field.");

        _accumulators = new();
        foreach (var elem in stageDoc.Elements)
        {
            if (elem.Name != "_id")
                _accumulators[elem.Name] = elem.Value;
        }
    }

    public IEnumerable<BsonDocument> Execute(IEnumerable<BsonDocument> input)
    {
        var groups = new Dictionary<string, GroupAccumulator>();
        var keyOrder = new List<string>();

        foreach (var doc in input)
        {
            var groupKey = ComputeGroupKey(doc);
            if (!groups.ContainsKey(groupKey))
            {
                groups[groupKey] = new GroupAccumulator();
                keyOrder.Add(groupKey);
            }

            var accumulator = groups[groupKey];
            accumulator.Add(doc, _accumulators);
        }

        foreach (var key in keyOrder)
        {
            var result = groups[key].Build(_idSpec, key);
            groups[key].ApplyAccumulators(result, _accumulators);
            yield return result;
        }
    }

    private string ComputeGroupKey(BsonDocument doc)
    {
        return GroupKeyHelper.ComputeKeyString(_idSpec, doc);
    }

    private sealed class GroupAccumulator
    {
        private readonly List<BsonDocument> _docs = new();

        public void Add(BsonDocument doc, Dictionary<string, BsonValue> accumulators)
        {
            _docs.Add(doc);
        }

        public BsonDocument Build(BsonValue idSpec, string key)
        {
            var result = new BsonDocument();
            result["_id"] = GroupKeyHelper.MaterializeKeyValue(idSpec, _docs[0]);
            return result;
        }

        public void ApplyAccumulators(BsonDocument result, Dictionary<string, BsonValue> accumulators)
        {
            foreach (var (fieldName, accSpec) in accumulators)
            {
                if (!accSpec.IsBsonDocument || ((BsonDocument)accSpec).ElementCount != 1)
                    throw new NotSupportedException($"Unsupported accumulator specification for field '{fieldName}'.");

                var accElem = ((BsonDocument)accSpec).GetElement(0);
                result[fieldName] = accElem.Name switch
                {
                    "$sum" => Accumulators.Sum(accElem.Value, _docs),
                    "$avg" => Accumulators.Avg(accElem.Value, _docs),
                    "$min" => Accumulators.MinMax(accElem.Value, _docs, min: true),
                    "$max" => Accumulators.MinMax(accElem.Value, _docs, min: false),
                    "$first" => _docs.Count > 0 ? ExpressionEvaluator.Evaluate(accElem.Value, _docs[0]) : BsonNull.Value,
                    "$last" => _docs.Count > 0 ? ExpressionEvaluator.Evaluate(accElem.Value, _docs[^1]) : BsonNull.Value,
                    "$push" => new BsonArray(_docs.Select(d => ExpressionEvaluator.Evaluate(accElem.Value, d))),
                    "$addToSet" => new BsonArray(_docs.Select(d => ExpressionEvaluator.Evaluate(accElem.Value, d)).Distinct()),
                    _ => throw new NotSupportedException($"Unsupported accumulator: {accElem.Name}")
                };
            }
        }
    }
}
