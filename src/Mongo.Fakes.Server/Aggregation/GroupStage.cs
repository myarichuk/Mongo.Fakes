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
        if (_idSpec.BsonType == BsonType.Null)
            return "null";

        if (_idSpec.IsString && _idSpec.AsString.StartsWith("$"))
        {
            var value = BsonPath.GetValue(doc, _idSpec.AsString[1..]) ?? BsonNull.Value;
            return value.ToString() ?? "null";
        }

        if (_idSpec.IsInt32 || _idSpec.IsInt64 || _idSpec.IsDouble || _idSpec.IsBoolean)
            return _idSpec.ToString() ?? "null";

        if (_idSpec.IsBsonDocument)
        {
            var spec = (BsonDocument)_idSpec;
            var keyParts = new List<string>();
            foreach (var elem in spec.Elements)
            {
                if (elem.Value.IsString && elem.Value.AsString.StartsWith("$"))
                {
                    var fieldValue = BsonPath.GetValue(doc, elem.Value.AsString[1..]) ?? BsonNull.Value;
                    keyParts.Add($"{elem.Name}:{fieldValue}");
                }
                else
                {
                    keyParts.Add($"{elem.Name}:{elem.Value}");
                }
            }
            return string.Join("|", keyParts);
        }

        return _idSpec.ToString() ?? "null";
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

            if (idSpec.BsonType == BsonType.Null)
                result["_id"] = BsonNull.Value;
            else if (idSpec.IsString && idSpec.AsString.StartsWith("$"))
                result["_id"] = BsonPath.GetValue(_docs[0], idSpec.AsString[1..]) ?? BsonNull.Value;
            else if (idSpec.IsBsonDocument)
            {
                var idDoc = new BsonDocument();
                var spec = (BsonDocument)idSpec;
                foreach (var elem in spec.Elements)
                {
                    if (elem.Value.IsString && elem.Value.AsString.StartsWith("$"))
                        idDoc[elem.Name] = BsonPath.GetValue(_docs[0], elem.Value.AsString[1..]) ?? BsonNull.Value;
                    else
                        idDoc[elem.Name] = elem.Value;
                }
                result["_id"] = idDoc;
            }
            else
                result["_id"] = idSpec;

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
                    "$sum" => Sum(accElem.Value),
                    "$avg" => Avg(accElem.Value),
                    "$min" => MinMax(accElem.Value, min: true),
                    "$max" => MinMax(accElem.Value, min: false),
                    "$first" => _docs.Count > 0 ? ExpressionEvaluator.Evaluate(accElem.Value, _docs[0]) : BsonNull.Value,
                    "$last" => _docs.Count > 0 ? ExpressionEvaluator.Evaluate(accElem.Value, _docs[^1]) : BsonNull.Value,
                    "$push" => new BsonArray(_docs.Select(d => ExpressionEvaluator.Evaluate(accElem.Value, d))),
                    "$addToSet" => new BsonArray(_docs.Select(d => ExpressionEvaluator.Evaluate(accElem.Value, d)).Distinct()),
                    _ => throw new NotSupportedException($"Unsupported accumulator: {accElem.Name}")
                };
            }
        }

        private BsonValue Sum(BsonValue expr)
        {
            long intTotal = 0;
            double doubleTotal = 0;
            bool isDouble = false;
            bool overflowedInt64 = false;

            foreach (var doc in _docs)
            {
                var value = ExpressionEvaluator.Evaluate(expr, doc);
                if (!value.IsNumeric)
                    continue;

                if (!isDouble && (value.IsInt32 || value.IsInt64))
                {
                    long asLong = value.ToInt64();
                    try
                    {
                        intTotal = checked(intTotal + asLong);
                    }
                    catch (OverflowException)
                    {
                        overflowedInt64 = true;
                    }
                }
                else
                {
                    isDouble = true;
                }

                doubleTotal += value.ToDouble();
            }

            if (isDouble || overflowedInt64)
                return new BsonDouble(doubleTotal);

            return intTotal is >= int.MinValue and <= int.MaxValue
                ? new BsonInt32((int)intTotal)
                : new BsonInt64(intTotal);
        }

        private BsonValue Avg(BsonValue expr)
        {
            if (_docs.Count == 0)
                return BsonNull.Value;

            double total = 0;
            int count = 0;
            foreach (var doc in _docs)
            {
                var value = ExpressionEvaluator.Evaluate(expr, doc);
                if (value.IsNumeric)
                {
                    total += value.ToDouble();
                    count++;
                }
            }
            return count == 0 ? BsonNull.Value : new BsonDouble(total / count);
        }

        private BsonValue MinMax(BsonValue expr, bool min)
        {
            BsonValue? result = null;
            foreach (var doc in _docs)
            {
                var value = ExpressionEvaluator.Evaluate(expr, doc);
                if (value.BsonType == BsonType.Null)
                    continue;

                if (result == null || (min ? value.CompareTo(result) < 0 : value.CompareTo(result) > 0))
                    result = value;
            }
            return result ?? BsonNull.Value;
        }
    }
}
