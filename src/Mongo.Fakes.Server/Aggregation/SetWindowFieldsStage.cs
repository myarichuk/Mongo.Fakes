using Mongo.Fakes.Core;
using MongoDB.Bson;
using Mongo.Fakes.Server.Query;

namespace Mongo.Fakes.Server.Aggregation;

/// <summary>
/// Implements the $setWindowFields aggregation stage.
/// Partitions documents and applies window functions within each partition.
/// </summary>
internal sealed class SetWindowFieldsStage
{
    private readonly BsonValue _partitionBy;
    private readonly BsonDocument? _sortBy;
    private readonly Dictionary<string, BsonDocument> _outputFields;

    public SetWindowFieldsStage(BsonDocument stageDoc)
    {
        _partitionBy = stageDoc.TryGetValue("partitionBy", out var pb) ? pb : BsonNull.Value;
        _sortBy = stageDoc.TryGetValue("sortBy", out var sb) && sb.IsBsonDocument ? (BsonDocument)sb : null;

        _outputFields = new();
        if (stageDoc.TryGetValue("output", out var output) && output.IsBsonDocument)
        {
            var outputDoc = (BsonDocument)output;
            foreach (var elem in outputDoc.Elements)
            {
                if (elem.Value.IsBsonDocument)
                    _outputFields[elem.Name] = (BsonDocument)elem.Value;
            }
        }
    }

    public IEnumerable<BsonDocument> Execute(IEnumerable<BsonDocument> input)
    {
        var documents = input.ToList();

        // Partition documents
        var partitions = PartitionDocuments(documents);

        // Process each partition
        foreach (var partition in partitions)
        {
            // Sort within partition if sortBy is specified
            var sortedDocs = _sortBy != null && _sortBy.ElementCount > 0
                ? partition.OrderBy(d => d, new BsonDocumentSortComparer(_sortBy)).ToList()
                : partition;

            // Apply window functions to each document
            for (int i = 0; i < sortedDocs.Count; i++)
            {
                var originalDoc = sortedDocs[i];
                var resultDoc = (BsonDocument)originalDoc.DeepClone();

                // Compute each output field
                foreach (var (fieldName, fieldSpec) in _outputFields)
                {
                    // Find the window operator (skip "window" element if present)
                    BsonElement? opElem = null;
                    foreach (var elem in fieldSpec.Elements)
                    {
                        if (elem.Name != "window")
                        {
                            opElem = elem;
                            break;
                        }
                    }

                    if (opElem.HasValue)
                    {
                        var op = opElem.Value;
                        var value = op.Name switch
                        {
                            "$documentNumber" => new BsonInt32(i + 1),
                            "$rank" => ComputeRank(i, sortedDocs),
                            "$sum" => ComputeWindowAccumulator(i, sortedDocs, fieldSpec, accType: "sum"),
                            "$avg" => ComputeWindowAccumulator(i, sortedDocs, fieldSpec, accType: "avg"),
                            "$min" => ComputeWindowAccumulator(i, sortedDocs, fieldSpec, accType: "min"),
                            "$max" => ComputeWindowAccumulator(i, sortedDocs, fieldSpec, accType: "max"),
                            "$first" => ComputeWindowAccumulator(i, sortedDocs, fieldSpec, accType: "first"),
                            "$last" => ComputeWindowAccumulator(i, sortedDocs, fieldSpec, accType: "last"),
                            _ => BsonNull.Value
                        };

                        BsonPath.SetValueByPath(resultDoc, fieldName, value);
                    }
                }

                yield return resultDoc;
            }
        }
    }

    private List<List<BsonDocument>> PartitionDocuments(List<BsonDocument> documents)
    {
        if (_partitionBy.BsonType == BsonType.Null)
        {
            return new List<List<BsonDocument>> { documents };
        }

        var partitionMap = new Dictionary<string, List<BsonDocument>>();
        var partitionOrder = new List<string>();

        foreach (var doc in documents)
        {
            var partKey = GroupKeyHelper.ComputeKeyString(_partitionBy, doc);
            if (!partitionMap.ContainsKey(partKey))
            {
                partitionMap[partKey] = new List<BsonDocument>();
                partitionOrder.Add(partKey);
            }
            partitionMap[partKey].Add(doc);
        }

        return partitionOrder.Select(key => partitionMap[key]).ToList();
    }

    private BsonValue ComputeRank(int currentIndex, List<BsonDocument> sortedDocs)
    {
        if (_sortBy == null || _sortBy.ElementCount == 0)
        {
            // Without sortBy, all documents have the same rank
            return new BsonInt32(1);
        }

        var comparer = new BsonDocumentSortComparer(_sortBy);
        int rank = 1;

        // Count how many documents before currentIndex are NOT equal to sortedDocs[currentIndex]
        for (int i = 0; i < currentIndex; i++)
        {
            if (comparer.Compare(sortedDocs[i], sortedDocs[currentIndex]) != 0)
            {
                rank++;
            }
        }

        return new BsonInt32(rank);
    }

    private BsonValue ComputeWindowAccumulator(int currentIndex, List<BsonDocument> sortedDocs,
        BsonDocument fieldSpec, string accType)
    {
        var windowSpec = fieldSpec.TryGetValue("window", out var w) && w.IsBsonDocument
            ? (BsonDocument)w
            : null;

        var (startIdx, endIdx) = ComputeWindowBounds(currentIndex, sortedDocs.Count, windowSpec);

        var windowDocs = sortedDocs.GetRange(startIdx, endIdx - startIdx + 1);

        if (windowDocs.Count == 0)
            return BsonNull.Value;

        var opElem = fieldSpec.GetElement(0);
        var expr = opElem.Value;

        return accType switch
        {
            "sum" => Accumulators.Sum(expr, windowDocs),
            "avg" => Accumulators.Avg(expr, windowDocs),
            "min" => Accumulators.MinMax(expr, windowDocs, min: true),
            "max" => Accumulators.MinMax(expr, windowDocs, min: false),
            "first" => windowDocs.Count > 0 ? ExpressionEvaluator.Evaluate(expr, windowDocs[0]) : BsonNull.Value,
            "last" => windowDocs.Count > 0 ? ExpressionEvaluator.Evaluate(expr, windowDocs[^1]) : BsonNull.Value,
            _ => BsonNull.Value
        };
    }

    private (int StartIdx, int EndIdx) ComputeWindowBounds(int currentIndex, int partitionSize,
        BsonDocument? windowSpec)
    {
        if (windowSpec == null)
        {
            // Default window
            if (_sortBy != null && _sortBy.ElementCount > 0)
            {
                // Running window: from start to current
                return (0, currentIndex);
            }
            else
            {
                // Whole partition
                return (0, partitionSize - 1);
            }
        }

        int startIdx = 0;
        int endIdx = partitionSize - 1;

        if (windowSpec.TryGetValue("documents", out var docsValue) && docsValue.IsBsonArray)
        {
            var docsArray = (BsonArray)docsValue;
            if (docsArray.Count >= 2)
            {
                // Parse [lower, upper]
                var lower = docsArray[0];
                var upper = docsArray[1];

                startIdx = ParseBound(lower, currentIndex, partitionSize, isStart: true);
                endIdx = ParseBound(upper, currentIndex, partitionSize, isStart: false);
            }
        }

        // Clamp to partition bounds
        startIdx = Math.Max(0, Math.Min(startIdx, partitionSize - 1));
        endIdx = Math.Max(0, Math.Min(endIdx, partitionSize - 1));

        return (startIdx, endIdx);
    }

    private int ParseBound(BsonValue boundValue, int currentIndex, int partitionSize, bool isStart)
    {
        if (boundValue.IsString && boundValue.AsString == "unbounded")
        {
            return isStart ? 0 : partitionSize - 1;
        }

        if (boundValue.IsString && boundValue.AsString == "current")
        {
            return currentIndex;
        }

        if (boundValue.IsInt32 || boundValue.IsInt64)
        {
            int offset = boundValue.ToInt32();
            return currentIndex + offset;
        }

        return isStart ? 0 : partitionSize - 1;
    }
}
