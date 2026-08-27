using Mongo.Fakes.Core;
using MongoDB.Bson;

namespace Mongo.Fakes.Core.Tests;

public class FilterCompilerTests
{
    private readonly FilterCompiler _compiler = new();

    [Fact]
    public void Compile_SimpleEquality_MatchesDocument()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ status: 'active' }"));

        Assert.True(predicate(BsonDocument.Parse("{ status: 'active' }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'inactive' }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_ComparisonOperator_EvaluatesCorrectly()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ age: { $gt: 18 } }"));

        Assert.True(predicate(BsonDocument.Parse("{ age: 25 }")));
        Assert.False(predicate(BsonDocument.Parse("{ age: 10 }")));
        Assert.False(predicate(BsonDocument.Parse("{ age: null }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_ImplicitAnd_RequiresAllFields()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ status: 'active', age: { $gte: 18 } }"));

        Assert.True(predicate(BsonDocument.Parse("{ status: 'active', age: 21 }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'active', age: 10 }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'inactive', age: 21 }")));
    }

    [Fact]
    public void Compile_ArrayField_UnwindsImplicitly()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ tags: 'admin' }"));

        Assert.True(predicate(BsonDocument.Parse("{ tags: ['admin', 'developer'] }")));
        Assert.True(predicate(BsonDocument.Parse("{ tags: 'admin' }")));
        Assert.False(predicate(BsonDocument.Parse("{ tags: ['developer'] }")));
    }

    [Fact]
    public void Compile_NullVsMissing_Distinguishes()
    {
        var predExists = _compiler.Compile(BsonDocument.Parse("{ x: { $exists: true } }"));
        var predNull = _compiler.Compile(BsonDocument.Parse("{ x: null }"));

        var docNull = BsonDocument.Parse("{ x: null }");
        var docMissing = BsonDocument.Parse("{ }");

        Assert.True(predExists(docNull));
        Assert.False(predExists(docMissing));

        Assert.True(predNull(docNull));
        Assert.True(predNull(docMissing));
    }

    [Fact]
    public void Compile_In_MatchesMembership()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ status: { $in: ['active', 'pending'] } }"));

        Assert.True(predicate(BsonDocument.Parse("{ status: 'active' }")));
        Assert.True(predicate(BsonDocument.Parse("{ status: 'pending' }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'inactive' }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_Nin_MissingFieldMatches()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ status: { $nin: ['active', 'pending'] } }"));

        Assert.False(predicate(BsonDocument.Parse("{ status: 'active' }")));
        Assert.True(predicate(BsonDocument.Parse("{ status: 'inactive' }")));
        Assert.True(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_LogicalOr_CombinesConditions()
    {
        var predicate = _compiler.Compile(
            BsonDocument.Parse("{ $or: [ { status: 'active' }, { status: 'pending' } ] }"));

        Assert.True(predicate(BsonDocument.Parse("{ status: 'active' }")));
        Assert.True(predicate(BsonDocument.Parse("{ status: 'pending' }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'inactive' }")));
    }

    [Fact]
    public void Compile_LogicalAndExplicit_CombinesConditions()
    {
        var predicate = _compiler.Compile(
            BsonDocument.Parse("{ $and: [ { status: 'active' }, { age: { $gt: 18 } } ] }"));

        Assert.True(predicate(BsonDocument.Parse("{ status: 'active', age: 25 }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'active', age: 10 }")));
    }

    [Fact]
    public void Compile_LogicalNor_ExcludesAllConditions()
    {
        var predicate = _compiler.Compile(
            BsonDocument.Parse("{ $nor: [ { status: 'active' }, { status: 'pending' } ] }"));

        Assert.False(predicate(BsonDocument.Parse("{ status: 'active' }")));
        Assert.False(predicate(BsonDocument.Parse("{ status: 'pending' }")));
        Assert.True(predicate(BsonDocument.Parse("{ status: 'inactive' }")));
    }

    [Fact]
    public void Compile_LogicalNor_MatchesDocumentMissingField()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ $nor: [ { a: 1 } ] }"));

        Assert.True(predicate(BsonDocument.Parse("{ }")));
        Assert.False(predicate(BsonDocument.Parse("{ a: 1 }")));
    }

    [Fact]
    public void Compile_Not_NegatesCondition()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ age: { $not: { $gt: 18 } } }"));

        Assert.True(predicate(BsonDocument.Parse("{ age: 10 }")));
        Assert.False(predicate(BsonDocument.Parse("{ age: 25 }")));
    }

    [Fact]
    public void Compile_Type_MatchesBsonType()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ x: { $type: 'string' } }"));

        Assert.True(predicate(BsonDocument.Parse("{ x: 'hello' }")));
        Assert.False(predicate(BsonDocument.Parse("{ x: 5 }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_Type_NumberAliasMatchesAllNumericTypes()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ x: { $type: 'number' } }"));

        Assert.True(predicate(new BsonDocument("x", 5)));
        Assert.True(predicate(new BsonDocument("x", 5L)));
        Assert.True(predicate(new BsonDocument("x", 5.5)));
        Assert.False(predicate(BsonDocument.Parse("{ x: 'nope' }")));
    }

    [Fact]
    public void Compile_Regex_MatchesPatternWithOptions()
    {
        var predicate = _compiler.Compile(
            BsonDocument.Parse("{ email: { $regex: '.*@company\\\\.com$', $options: 'i' } }"));

        Assert.True(predicate(BsonDocument.Parse("{ email: 'alice@COMPANY.com' }")));
        Assert.False(predicate(BsonDocument.Parse("{ email: 'alice@other.com' }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_Regex_LiteralForm_MatchesPattern()
    {
        // The driver represents Builders<T>.Filter.Regex(...) as a BsonRegularExpression
        // value directly (extended JSON `/pattern/opts`), not a { $regex, $options } document.
        var filter = new BsonDocument("name", new BsonRegularExpression("^A", "i"));
        var predicate = _compiler.Compile(filter);

        Assert.True(predicate(BsonDocument.Parse("{ name: 'alice' }")));
        Assert.False(predicate(BsonDocument.Parse("{ name: 'bob' }")));
    }

    [Fact]
    public void Compile_Not_With_BareRegex_NegatesMatch()
    {
        var filter = new BsonDocument("name", new BsonDocument("$not", new BsonRegularExpression("^A", "i")));
        var predicate = _compiler.Compile(filter);

        Assert.False(predicate(BsonDocument.Parse("{ name: 'alice' }")));
        Assert.True(predicate(BsonDocument.Parse("{ name: 'bob' }")));
        Assert.True(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_Not_With_RegexDocument_NegatesMatch()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ name: { $not: { $regex: '^A', $options: 'i' } } }"));

        Assert.False(predicate(BsonDocument.Parse("{ name: 'alice' }")));
        Assert.True(predicate(BsonDocument.Parse("{ name: 'bob' }")));
        Assert.True(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_All_RequiresEveryValuePresent()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ tags: { $all: ['admin', 'user'] } }"));

        Assert.True(predicate(BsonDocument.Parse("{ tags: ['admin', 'user', 'extra'] }")));
        Assert.False(predicate(BsonDocument.Parse("{ tags: ['admin'] }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }

    [Fact]
    public void Compile_ElemMatch_ScalarForm_MatchesArrayElement()
    {
        var predicate = _compiler.Compile(
            BsonDocument.Parse("{ scores: { $elemMatch: { $gt: 80 } } }"));

        Assert.True(predicate(BsonDocument.Parse("{ scores: [70, 90] }")));
        Assert.False(predicate(BsonDocument.Parse("{ scores: [70, 75] }")));
    }

    [Fact]
    public void Compile_ElemMatch_DocumentForm_MatchesArrayElement()
    {
        var predicate = _compiler.Compile(
            BsonDocument.Parse("{ users: { $elemMatch: { age: { $gt: 20 }, status: 'active' } } }"));

        Assert.True(predicate(BsonDocument.Parse(
            "{ users: [ { age: 25, status: 'active' }, { age: 15, status: 'active' } ] }")));
        Assert.False(predicate(BsonDocument.Parse(
            "{ users: [ { age: 25, status: 'inactive' } ] }")));
    }

    [Fact]
    public void Compile_UnknownOperator_ThrowsAtCompileTime()
    {
        Assert.Throws<NotSupportedException>(() =>
            _compiler.Compile(BsonDocument.Parse("{ x: { $foobar: 5 } }")));
    }

    [Fact]
    public void Compile_UnknownTopLevelOperator_ThrowsAtCompileTime()
    {
        Assert.Throws<NotSupportedException>(() =>
            _compiler.Compile(BsonDocument.Parse("{ $foobar: [ { x: 1 } ] }")));
    }

    [Fact]
    public void Compile_DotNotation_TraversesNestedDocuments()
    {
        var predicate = _compiler.Compile(BsonDocument.Parse("{ 'address.city': 'NYC' }"));

        Assert.True(predicate(BsonDocument.Parse("{ address: { city: 'NYC' } }")));
        Assert.False(predicate(BsonDocument.Parse("{ address: { city: 'LA' } }")));
        Assert.False(predicate(BsonDocument.Parse("{ }")));
    }
}
