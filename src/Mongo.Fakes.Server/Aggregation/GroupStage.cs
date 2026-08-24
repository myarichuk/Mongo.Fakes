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
            yield return groups[key].Build(_idSpec, key);
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
    }
}
