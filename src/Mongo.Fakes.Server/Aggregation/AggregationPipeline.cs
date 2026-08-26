using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Query;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class AggregationPipeline
{
    private readonly FilterCompiler _filterCompiler = new();
    private readonly Func<string, IReadOnlyList<BsonDocument>>? _resolveCollection;
    private readonly IReadOnlyDictionary<string, BsonValue>? _variables;
    private readonly TextIndexSpec? _textIndex;

    public AggregationPipeline(Func<string, IReadOnlyList<BsonDocument>>? resolveCollection = null, IReadOnlyDictionary<string, BsonValue>? variables = null, TextIndexSpec? textIndex = null)
    {
        _resolveCollection = resolveCollection;
        _variables = variables;
        _textIndex = textIndex;
    }

    public IEnumerable<BsonDocument> Execute(IEnumerable<BsonDocument> data, BsonArray pipeline)
    {
        var current = data;

        foreach (var stageValue in pipeline)
        {
            if (!stageValue.IsBsonDocument)
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Pipeline stage must be a document");

            var stage = (BsonDocument)stageValue;
            if (stage.ElementCount != 1)
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Pipeline stage must have exactly one key");

            var stageElem = stage.GetElement(0);
            current = stageElem.Name switch
            {
                "$match" => ExecuteMatch(current, (BsonDocument)stageElem.Value),
                "$project" => ExecuteProject(current, (BsonDocument)stageElem.Value),
                "$sort" => ExecuteSort(current, (BsonDocument)stageElem.Value),
                "$skip" => ExecuteSkip(current, stageElem.Value),
                "$limit" => ExecuteLimit(current, stageElem.Value),
                "$group" => ExecuteGroup(current, (BsonDocument)stageElem.Value),
                "$unwind" => ExecuteUnwind(current, stageElem.Value),
                "$count" => ExecuteCount(current, stageElem.Value),
                "$addFields" => ExecuteAddFields(current, (BsonDocument)stageElem.Value),
                "$set" => ExecuteAddFields(current, (BsonDocument)stageElem.Value),
                "$replaceRoot" => ExecuteReplaceRoot(current, (BsonDocument)stageElem.Value),
                "$lookup" => ExecuteLookup(current, (BsonDocument)stageElem.Value),
                "$setWindowFields" => ExecuteSetWindowFields(current, (BsonDocument)stageElem.Value),
                _ => throw new MongoCommandException(ErrorCodes.UnrecognizedPipelineStage, "UnrecognizedPipelineStage", $"Unknown stage: {stageElem.Name}")
            };
        }

        // Strip hidden text score field as final step
        current = current.Select(d =>
        {
            var doc = (BsonDocument)d.DeepClone();
            doc.Remove(Query.TextSearchFilter.ScoreField);
            return doc;
        });

        return current;
    }

    private IEnumerable<BsonDocument> ExecuteMatch(IEnumerable<BsonDocument> data, BsonDocument filter)
    {
        var results = data;

        // Apply text search if present
        if (TextSearchFilter.TryExtract(filter, out var searchTerms, out var remainingFilter))
        {
            results = TextSearchFilter.Apply(results, searchTerms!, _textIndex);
            filter = remainingFilter;
        }

        var predicate = _filterCompiler.Compile(filter, _variables);
        results = results.Where(predicate);

        return results;
    }

    private IEnumerable<BsonDocument> ExecuteProject(IEnumerable<BsonDocument> data, BsonDocument projection)
    {
        if (projection.ElementCount == 0)
            return data;

        var projector = new Projector(projection);
        return data.Select(d => projector.Project(d));
    }

    private IEnumerable<BsonDocument> ExecuteSort(IEnumerable<BsonDocument> data, BsonDocument sortSpec)
    {
        if (sortSpec.ElementCount == 0)
            return data;

        var comparer = new BsonDocumentSortComparer(sortSpec);
        return data.OrderBy(d => d, comparer);
    }

    private IEnumerable<BsonDocument> ExecuteSkip(IEnumerable<BsonDocument> data, BsonValue skipValue)
    {
        int skip = skipValue.ToInt32();
        if (skip < 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$skip must be >= 0");
        return data.Skip(skip);
    }

    private IEnumerable<BsonDocument> ExecuteLimit(IEnumerable<BsonDocument> data, BsonValue limitValue)
    {
        int limit = limitValue.ToInt32();
        if (limit < 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$limit must be >= 0");
        return data.Take(limit);
    }

    private IEnumerable<BsonDocument> ExecuteGroup(IEnumerable<BsonDocument> data, BsonDocument stageDoc)
    {
        var groupStage = new GroupStage(stageDoc);
        return groupStage.Execute(data);
    }

    private IEnumerable<BsonDocument> ExecuteSetWindowFields(IEnumerable<BsonDocument> data, BsonDocument stageDoc)
    {
        var windowStage = new SetWindowFieldsStage(stageDoc);
        return windowStage.Execute(data);
    }

    private IEnumerable<BsonDocument> ExecuteUnwind(IEnumerable<BsonDocument> data, BsonValue spec)
    {
        var unwindStage = new UnwindStage(spec);
        return unwindStage.Execute(data);
    }

    private IEnumerable<BsonDocument> ExecuteCount(IEnumerable<BsonDocument> data, BsonValue countValue)
    {
        if (!countValue.IsString)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$count must be a string");

        string fieldName = countValue.AsString;
        int count = data.Count();

        return new[] { new BsonDocument { { fieldName, count } } };
    }

    private IEnumerable<BsonDocument> ExecuteAddFields(IEnumerable<BsonDocument> data, BsonDocument fieldSpec)
    {
        return data.Select(doc =>
        {
            var result = (BsonDocument)doc.DeepClone();
            foreach (var elem in fieldSpec.Elements)
            {
                var value = ExpressionEvaluator.Evaluate(elem.Value, doc, _variables);
                BsonPath.SetValueByPath(result, elem.Name, value);
            }
            return result;
        });
    }

    private IEnumerable<BsonDocument> ExecuteReplaceRoot(IEnumerable<BsonDocument> data, BsonDocument stageDoc)
    {
        if (!stageDoc.TryGetValue("newRoot", out var rootExpr))
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$replaceRoot requires 'newRoot' field");

        return data.Select(doc =>
        {
            var newRoot = ExpressionEvaluator.Evaluate(rootExpr, doc, _variables);
            if (!newRoot.IsBsonDocument)
                throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$replaceRoot newRoot must be a document");
            return (BsonDocument)newRoot;
        });
    }

    private IEnumerable<BsonDocument> ExecuteLookup(IEnumerable<BsonDocument> data, BsonDocument stageDoc)
    {
        if (_resolveCollection == null)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "$lookup requires collection resolver (internal error)");

        var lookupStage = new LookupStage(stageDoc, _resolveCollection);
        return lookupStage.Execute(data);
    }
}
