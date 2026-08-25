using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Server.Query;

internal sealed class BsonDocumentSortComparer : IComparer<BsonDocument>
{
    private readonly List<(string Path, int Direction, bool IsTextScore)> _sortFields;

    public BsonDocumentSortComparer(BsonDocument sortSpec)
    {
        _sortFields = new();
        foreach (var element in sortSpec.Elements)
        {
            bool isTextScore = false;
            int direction = 1;

            // Check if value is { $meta: "textScore" }
            if (element.Value is BsonDocument metaDoc &&
                metaDoc.ElementCount == 1 &&
                metaDoc.TryGetValue("$meta", out var metaValue) &&
                metaValue.IsString &&
                metaValue.AsString == "textScore")
            {
                isTextScore = true;
            }
            else
            {
                direction = element.Value.ToInt32();
            }

            _sortFields.Add((element.Name, direction, isTextScore));
        }
    }

    public int Compare(BsonDocument? left, BsonDocument? right)
    {
        if (left == null && right == null)
            return 0;
        if (left == null)
            return -1;
        if (right == null)
            return 1;

        foreach (var (path, direction, isTextScore) in _sortFields)
        {
            var leftValue = BsonPath.GetValue(left, path) ?? BsonNull.Value;
            var rightValue = BsonPath.GetValue(right, path) ?? BsonNull.Value;

            int cmp = leftValue.CompareTo(rightValue);
            if (cmp != 0)
            {
                // textScore always sorts descending (highest score first)
                if (isTextScore)
                    return -cmp;
                return direction == 1 ? cmp : -cmp;
            }
        }

        return 0;
    }
}
