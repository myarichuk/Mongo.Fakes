using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Errors;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class LookupStage
{
    private readonly string _from;
    private readonly string _as;
    private readonly string? _localField;
    private readonly string? _foreignField;
    private readonly BsonDocument? _let;
    private readonly BsonArray? _pipeline;
    private readonly Func<string, IReadOnlyList<BsonDocument>> _resolveCollection;

    public LookupStage(BsonDocument spec, Func<string, IReadOnlyList<BsonDocument>> resolveCollection)
    {
        _resolveCollection = resolveCollection;

        // Parse required fields
        if (!spec.TryGetValue("from", out var fromValue) || !fromValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup 'from' must be a string");
        _from = fromValue.AsString;

        if (!spec.TryGetValue("as", out var asValue) || !asValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup 'as' must be a string");
        _as = asValue.AsString;

        // Parse optional fields
        spec.TryGetValue("localField", out var localFieldValue);
        spec.TryGetValue("foreignField", out var foreignFieldValue);
        spec.TryGetValue("let", out var letValue);
        spec.TryGetValue("pipeline", out var pipelineValue);

        // Validate localField/foreignField consistency
        bool hasLocal = localFieldValue != null && localFieldValue.IsString;
        bool hasForeign = foreignFieldValue != null && foreignFieldValue.IsString;

        if (hasLocal != hasForeign)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup requires both 'localField' and 'foreignField' or neither");

        if (hasLocal)
        {
            _localField = localFieldValue!.AsString;
            _foreignField = foreignFieldValue!.AsString;
        }

        if (letValue != null && !letValue.IsBsonDocument)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup 'let' must be a document");
        _let = letValue as BsonDocument;

        if (pipelineValue != null && !pipelineValue.IsBsonArray)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup 'pipeline' must be an array");
        _pipeline = pipelineValue as BsonArray;

        // Require at least one of (localField+foreignField) or pipeline
        if (!hasLocal && _pipeline == null)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup requires either 'localField'/'foreignField' or 'pipeline'");
    }

    public IEnumerable<BsonDocument> Execute(IEnumerable<BsonDocument> input)
    {
        var foreignDocs = _resolveCollection(_from);

        if (_pipeline != null)
        {
            // Sub-pipeline form
            return ExecuteSubPipeline(input, foreignDocs);
        }
        else
        {
            // Equality-join form
            return ExecuteEqualityJoin(input, foreignDocs);
        }
    }

    private IEnumerable<BsonDocument> ExecuteEqualityJoin(IEnumerable<BsonDocument> input, IReadOnlyList<BsonDocument> foreignDocs)
    {
        // Build a bucketed index of foreign documents by foreignField value
        var foreignIndex = new Dictionary<string, List<BsonDocument>>();
        foreach (var doc in foreignDocs)
        {
            var keyValue = BsonPath.GetValue(doc, _foreignField!) ?? BsonNull.Value;
            string keyStr = keyValue.ToJson();

            if (!foreignIndex.TryGetValue(keyStr, out var bucket))
            {
                bucket = new List<BsonDocument>();
                foreignIndex[keyStr] = bucket;
            }
            bucket.Add(doc);
        }

        // Per input doc, lookup matching foreign docs
        foreach (var inputDoc in input)
        {
            var result = (BsonDocument)inputDoc.DeepClone();
            var localValue = BsonPath.GetValue(inputDoc, _localField!) ?? BsonNull.Value;
            var matchedDocs = new List<BsonDocument>();

            if (localValue.IsBsonArray)
            {
                // Array-valued localField: match any element
                var arrayValues = new HashSet<string>();
                foreach (var elem in (BsonArray)localValue)
                {
                    string keyStr = (elem ?? BsonNull.Value).ToJson();
                    if (foreignIndex.TryGetValue(keyStr, out var bucket))
                    {
                        foreach (var doc in bucket)
                        {
                            // Avoid duplicates based on reference identity (input order preserved)
                            if (!matchedDocs.Any(d => ReferenceEquals(d, doc)))
                                matchedDocs.Add(doc);
                        }
                    }
                }
            }
            else
            {
                // Scalar localField
                string keyStr = localValue.ToJson();
                if (foreignIndex.TryGetValue(keyStr, out var bucket))
                    matchedDocs.AddRange(bucket);
            }

            BsonPath.SetValueByPath(result, _as, new BsonArray(matchedDocs));
            yield return result;
        }
    }

    private IEnumerable<BsonDocument> ExecuteSubPipeline(IEnumerable<BsonDocument> input, IReadOnlyList<BsonDocument> foreignDocs)
    {
        foreach (var inputDoc in input)
        {
            var result = (BsonDocument)inputDoc.DeepClone();

            // Evaluate let bindings
            var variables = new Dictionary<string, BsonValue>();
            if (_let != null)
            {
                foreach (var elem in _let.Elements)
                {
                    var value = ExpressionEvaluator.Evaluate(elem.Value, inputDoc);
                    variables[elem.Name] = value;
                }
            }

            // Apply optional equality-join filter before sub-pipeline
            IEnumerable<BsonDocument> docsToProcess = foreignDocs;
            if (_localField != null && _foreignField != null)
            {
                var localValue = BsonPath.GetValue(inputDoc, _localField) ?? BsonNull.Value;
                docsToProcess = FilterByEquality(foreignDocs, localValue, _foreignField);
            }

            // Run sub-pipeline with bindings
            var pipeline = new AggregationPipeline(_resolveCollection, variables);
            var pipelineResults = pipeline.Execute(docsToProcess, _pipeline!).ToList();

            BsonPath.SetValueByPath(result, _as, new BsonArray(pipelineResults));
            yield return result;
        }
    }

    private static IEnumerable<BsonDocument> FilterByEquality(IReadOnlyList<BsonDocument> docs, BsonValue localValue, string foreignField)
    {
        if (localValue.IsBsonArray)
        {
            // Match any element
            var localSet = new HashSet<string>();
            foreach (var elem in (BsonArray)localValue)
                localSet.Add((elem ?? BsonNull.Value).ToJson());

            foreach (var doc in docs)
            {
                var foreignValue = BsonPath.GetValue(doc, foreignField) ?? BsonNull.Value;
                if (localSet.Contains(foreignValue.ToJson()))
                    yield return doc;
            }
        }
        else
        {
            // Scalar localField: single equality check
            string localStr = localValue.ToJson();
            foreach (var doc in docs)
            {
                var foreignValue = BsonPath.GetValue(doc, foreignField) ?? BsonNull.Value;
                if (foreignValue.ToJson() == localStr)
                    yield return doc;
            }
        }
    }
}
