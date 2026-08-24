using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class UnwindStage
{
    private readonly string _path;
    private readonly bool _preserveNull;

    public UnwindStage(BsonValue spec)
    {
        if (spec.IsString)
        {
            _path = spec.AsString;
            if (!_path.StartsWith("$"))
                throw new InvalidOperationException("$unwind path must start with $");
            _path = _path[1..];
            _preserveNull = false;
        }
        else if (spec.IsBsonDocument)
        {
            var doc = (BsonDocument)spec;
            if (!doc.TryGetValue("path", out var pathValue) || !pathValue.IsString)
                throw new InvalidOperationException("$unwind requires path field");

            _path = pathValue.AsString;
            if (!_path.StartsWith("$"))
                throw new InvalidOperationException("$unwind path must start with $");
            _path = _path[1..];

            _preserveNull = doc.TryGetValue("preserveNullAndEmptyArrays", out var pnValue) && pnValue.ToBoolean();
        }
        else
            throw new InvalidOperationException("$unwind spec must be string or document");
    }

    public IEnumerable<BsonDocument> Execute(IEnumerable<BsonDocument> input)
    {
        foreach (var doc in input)
        {
            var value = BsonPath.GetValue(doc, _path);

            if (value == null || value.BsonType == BsonType.Null)
            {
                if (_preserveNull)
                    yield return doc;
                continue;
            }

            if (!value.IsBsonArray)
            {
                yield return doc;
                continue;
            }

            var array = (BsonArray)value;
            if (array.Count == 0)
            {
                if (_preserveNull)
                    yield return doc;
                continue;
            }

            foreach (var item in array)
            {
                var unwoundDoc = (BsonDocument)doc.DeepClone();
                BsonPath.SetValueByPath(unwoundDoc, _path, item);
                yield return unwoundDoc;
            }
        }
    }
}
