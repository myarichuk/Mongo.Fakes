using MongoDB.Bson;

namespace Mongo.Fakes.Server.Query;

/// <summary>
/// Equality for index keys (_id and unique indexes), following MongoDB's rule that
/// numerically equal values are the same key regardless of type (1, 1L and 1.0 collide).
/// </summary>
internal sealed class BsonKeyComparer : IEqualityComparer<BsonValue>
{
    public static readonly BsonKeyComparer Instance = new();

    public bool Equals(BsonValue? x, BsonValue? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;

        if (x.IsNumeric && y.IsNumeric)
        {
            if (IsIntegral(x) && IsIntegral(y))
                return x.ToInt64() == y.ToInt64();
            if (x.IsDecimal128 || y.IsDecimal128)
                return x.ToDecimal128() == y.ToDecimal128();
            return x.ToDouble() == y.ToDouble();
        }

        if (x is BsonArray xa && y is BsonArray ya)
        {
            if (xa.Count != ya.Count)
                return false;
            for (int i = 0; i < xa.Count; i++)
            {
                if (!Equals(xa[i], ya[i]))
                    return false;
            }
            return true;
        }

        if (x is BsonDocument xd && y is BsonDocument yd)
        {
            if (xd.ElementCount != yd.ElementCount)
                return false;
            for (int i = 0; i < xd.ElementCount; i++)
            {
                var xe = xd.GetElement(i);
                var ye = yd.GetElement(i);
                if (xe.Name != ye.Name || !Equals(xe.Value, ye.Value))
                    return false;
            }
            return true;
        }

        return x.Equals(y);
    }

    public int GetHashCode(BsonValue obj)
    {
        if (obj.IsNumeric)
            return obj.ToDouble().GetHashCode();

        if (obj is BsonArray array)
        {
            var hash = new HashCode();
            foreach (var item in array)
                hash.Add(GetHashCode(item));
            return hash.ToHashCode();
        }

        if (obj is BsonDocument doc)
        {
            var hash = new HashCode();
            foreach (var element in doc)
            {
                hash.Add(element.Name);
                hash.Add(GetHashCode(element.Value));
            }
            return hash.ToHashCode();
        }

        return obj.GetHashCode();
    }

    private static bool IsIntegral(BsonValue value) => value.IsInt32 || value.IsInt64;
}
