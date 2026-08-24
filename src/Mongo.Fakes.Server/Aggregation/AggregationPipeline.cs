using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Query;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Aggregation;

internal sealed class AggregationPipeline
{
    private readonly FilterCompiler _filterCompiler = new();

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
                _ => throw new MongoCommandException(ErrorCodes.UnrecognizedPipelineStage, "UnrecognizedPipelineStage", $"Unknown stage: {stageElem.Name}")
            };
        }

        return current;
    }

    private IEnumerable<BsonDocument> ExecuteMatch(IEnumerable<BsonDocument> data, BsonDocument filter)
    {
        var predicate = _filterCompiler.Compile(filter);
        return data.Where(predicate);
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

    private IEnumerable<BsonDocument> ExecuteUnwind(IEnumerable<BsonDocument> data, BsonValue spec)
    {
        var unwindStage = new UnwindStage(spec);
        return unwindStage.Execute(data);
    }
}
