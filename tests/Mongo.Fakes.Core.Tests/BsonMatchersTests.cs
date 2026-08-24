using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.Tests;

public class BsonMatchersTests
{
    [Fact]
    public void Eq_MissingField_MatchesOnlyNullExpected()
    {
        Assert.True(BsonMatchers.Eq(null, BsonNull.Value));
        Assert.False(BsonMatchers.Eq(null, "active"));
    }

    [Fact]
    public void Ne_IsExactNegationOfEq()
    {
        Assert.False(BsonMatchers.Ne(null, BsonNull.Value));
        Assert.True(BsonMatchers.Ne(null, "active"));
    }

    [Fact]
    public void In_MixedTypeArray_ComparesEachCorrectly()
    {
        var values = new BsonArray { 5, "admin", ObjectId.GenerateNewId() };

        Assert.True(BsonMatchers.In(new BsonInt32(5), values));
        Assert.True(BsonMatchers.In(new BsonString("admin"), values));
        Assert.False(BsonMatchers.In(new BsonString("other"), values));
        Assert.False(BsonMatchers.In(null, values));
    }

    [Fact]
    public void Gt_NullField_NeverMatches()
    {
        Assert.False(BsonMatchers.Gt(BsonNull.Value, 5));
        Assert.False(BsonMatchers.Gt(null, 5));
    }

    [Fact]
    public void Exists_DistinguishesMissingFromPresent()
    {
        Assert.True(BsonMatchers.Exists(BsonNull.Value, true));
        Assert.False(BsonMatchers.Exists(null, true));
        Assert.True(BsonMatchers.Exists(null, false));
    }
}
