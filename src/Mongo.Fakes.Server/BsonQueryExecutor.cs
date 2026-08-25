using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Query;
using MongoDB.Bson;

namespace Mongo.Fakes.Server;

public sealed class BsonQueryExecutor
{
    private readonly FilterCompiler _filterCompiler = new();

    public IEnumerable<BsonDocument> ExecuteFind(
        IEnumerable<BsonDocument> data,
        BsonDocument filter,
        BsonDocument? projection = null,
        BsonDocument? sort = null,
        int skip = 0,
        int limit = 0,
        TextIndexSpec? textIndex = null)
    {
        var results = data;

        // Apply text search if present
        if (TextSearchFilter.TryExtract(filter, out var searchTerms, out var remainingFilter))
        {
            results = TextSearchFilter.Apply(results, searchTerms!, textIndex);
            filter = remainingFilter;
        }

        var predicate = _filterCompiler.Compile(filter);
        results = results.Where(predicate);

        if (sort != null && sort.ElementCount > 0)
        {
            var comparer = new BsonDocumentSortComparer(sort);
            results = results.OrderBy(d => d, comparer);
        }

        if (skip > 0)
            results = results.Skip(skip);

        if (limit > 0)
            results = results.Take(limit);

        if (projection != null && projection.ElementCount > 0)
        {
            var projector = new Projector(projection);
            results = results.Select(d => projector.Project(d));
        }

        // Strip hidden text score field before returning
        results = results.Select(d =>
        {
            var doc = (BsonDocument)d.DeepClone();
            doc.Remove(TextSearchFilter.ScoreField);
            return doc;
        });

        return results;
    }

    public int ExecuteCount(IEnumerable<BsonDocument> data, BsonDocument filter, int skip = 0, int limit = 0, TextIndexSpec? textIndex = null)
    {
        var results = data;

        // Apply text search if present
        if (TextSearchFilter.TryExtract(filter, out var searchTerms, out var remainingFilter))
        {
            results = TextSearchFilter.Apply(results, searchTerms!, textIndex);
            filter = remainingFilter;
        }

        var predicate = _filterCompiler.Compile(filter);
        results = results.Where(predicate);

        if (skip > 0)
            results = results.Skip(skip);

        if (limit > 0)
            results = results.Take(limit);

        return results.Count();
    }
}
