using MongoDB.Bson;
using Mongo.Fakes.Core;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server.Query;

/// <summary>
/// Handles $text search filter extraction and application.
/// Text search attaches a hidden score field to matching documents.
/// </summary>
internal static class TextSearchFilter
{
    /// <summary>
    /// Hidden field used to store text search scores.
    /// </summary>
    public const string ScoreField = "__fakeTextScore";

    /// <summary>
    /// Attempts to extract $text from the filter document.
    /// Returns true if found, along with search terms and filter with $text removed.
    /// Input document is never mutated.
    /// </summary>
    public static bool TryExtract(BsonDocument filter, out string? searchTerms, out BsonDocument remainingFilter)
    {
        if (!filter.TryGetValue("$text", out var textValue) || textValue is not BsonDocument textDoc)
        {
            searchTerms = null;
            remainingFilter = filter;
            return false;
        }

        if (!textDoc.TryGetValue("$search", out var searchValue) || !searchValue.IsString)
        {
            searchTerms = null;
            remainingFilter = filter;
            return false;
        }

        searchTerms = searchValue.AsString;

        // Create a copy of filter without $text
        var remaining = new BsonDocument(filter);
        remaining.Remove("$text");
        remainingFilter = remaining;

        return true;
    }

    /// <summary>
    /// Applies text search to documents, attaching a score to matching docs.
    /// Throws IndexNotFound (code 27) if index is null.
    /// Never mutates input documents.
    /// </summary>
    public static IEnumerable<BsonDocument> Apply(
        IEnumerable<BsonDocument> data,
        string searchTerms,
        TextIndexSpec? index)
    {
        if (index == null)
        {
            throw new MongoCommandException(
                ErrorCodes.IndexNotFound,
                "IndexNotFound",
                "no text indexes for this collection");
        }

        // Split search terms into lowercase tokens (OR semantics)
        var searchTokens = SplitTerms(searchTerms);

        foreach (var doc in data)
        {
            double score = ComputeScore(doc, searchTokens, index);
            if (score > 0)
            {
                var matched = (BsonDocument)doc.DeepClone();
                matched[ScoreField] = new BsonDouble(score);
                yield return matched;
            }
        }
    }

    /// <summary>
    /// Splits search string into whitespace-separated, lowercase tokens.
    /// </summary>
    private static List<string> SplitTerms(string searchString)
    {
        return searchString
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// Computes text search score for a document.
    /// Score = sum of occurrence counts for each search term across indexed fields.
    /// </summary>
    private static double ComputeScore(BsonDocument doc, List<string> searchTokens, TextIndexSpec index)
    {
        double score = 0;

        if (index.IsWildcard)
        {
            // Wildcard: scan all string leaf values in the document
            score += ScanDocumentForTerms(doc, searchTokens);
        }
        else
        {
            // Named fields only
            foreach (var field in index.Fields)
            {
                var value = BsonPath.GetValue(doc, field);
                if (value != null && value.IsString)
                {
                    var tokens = SplitTerms(value.AsString);
                    score += CountTermOccurrences(tokens, searchTokens);
                }
            }
        }

        return score;
    }

    /// <summary>
    /// Recursively scans all string values in a document for search terms.
    /// </summary>
    private static double ScanDocumentForTerms(BsonValue value, List<string> searchTokens)
    {
        double score = 0;

        if (value.IsString)
        {
            var tokens = SplitTerms(value.AsString);
            score += CountTermOccurrences(tokens, searchTokens);
        }
        else if (value.IsBsonDocument)
        {
            var doc = value.AsBsonDocument;
            foreach (var element in doc.Elements)
            {
                score += ScanDocumentForTerms(element.Value, searchTokens);
            }
        }
        else if (value.IsBsonArray)
        {
            var arr = value.AsBsonArray;
            foreach (var elem in arr)
            {
                score += ScanDocumentForTerms(elem, searchTokens);
            }
        }

        return score;
    }

    /// <summary>
    /// Counts how many times any search term appears in the token list.
    /// </summary>
    private static double CountTermOccurrences(List<string> tokens, List<string> searchTerms)
    {
        double count = 0;
        foreach (var term in searchTerms)
        {
            count += tokens.Count(t => t == term);
        }
        return count;
    }
}
