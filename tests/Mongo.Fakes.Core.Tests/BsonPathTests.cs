using MongoDB.Bson;
using Xunit;

namespace Mongo.Fakes.Core.Tests;

public class BsonPathTests
{
    [Fact]
    public void GetValue_WithSimplePath_ReturnsValue()
    {
        var doc = new BsonDocument { { "name", "John" }, { "age", 30 } };
        var value = BsonPath.GetValue(doc, "name");
        Assert.Equal("John", value?.AsString);
    }

    [Fact]
    public void GetValue_WithNestedPath_ReturnsValue()
    {
        var doc = new BsonDocument
        {
            { "user", new BsonDocument { { "name", "John" }, { "age", 30 } } }
        };
        var value = BsonPath.GetValue(doc, "user.name");
        Assert.Equal("John", value?.AsString);
    }

    [Fact]
    public void GetValue_WithArrayIndex_ReturnsElement()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b", "c" } } };
        var value = BsonPath.GetValue(doc, "items.1");
        Assert.Equal("b", value?.AsString);
    }

    [Fact]
    public void GetValue_WithNegativeArrayIndex_ReturnsNull()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b", "c" } } };
        var value = BsonPath.GetValue(doc, "items.-1");
        Assert.Null(value);
    }

    [Fact]
    public void GetValue_WithOutOfRangeArrayIndex_ReturnsNull()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b", "c" } } };
        var value = BsonPath.GetValue(doc, "items.10");
        Assert.Null(value);
    }

    [Fact]
    public void GetValue_WithMissingField_ReturnsNull()
    {
        var doc = new BsonDocument { { "name", "John" } };
        var value = BsonPath.GetValue(doc, "missing");
        Assert.Null(value);
    }

    [Fact]
    public void SetValueByPath_WithSimplePath_SetsValue()
    {
        var doc = new BsonDocument();
        BsonPath.SetValueByPath(doc, "name", new BsonString("John"));
        Assert.Equal("John", doc["name"].AsString);
    }

    [Fact]
    public void SetValueByPath_WithNestedPath_CreatesStructure()
    {
        var doc = new BsonDocument();
        BsonPath.SetValueByPath(doc, "user.name", new BsonString("John"));
        Assert.Equal("John", doc["user"].AsBsonDocument["name"].AsString);
    }

    [Fact]
    public void SetValueByPath_WithArrayIndex_SetsElement()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b", "c" } } };
        BsonPath.SetValueByPath(doc, "items.1", new BsonString("b-updated"));
        Assert.Equal("b-updated", doc["items"].AsBsonArray[1].AsString);
    }

    [Fact]
    public void SetValueByPath_WithArrayIndexBeyondLength_GrowsArray()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b" } } };
        BsonPath.SetValueByPath(doc, "items.4", new BsonString("e"));

        var array = doc["items"].AsBsonArray;
        Assert.Equal(5, array.Count);
        Assert.Equal("a", array[0].AsString);
        Assert.Equal("b", array[1].AsString);
        Assert.Equal(BsonType.Null, array[2].BsonType);
        Assert.Equal(BsonType.Null, array[3].BsonType);
        Assert.Equal("e", array[4].AsString);
    }

    [Fact]
    public void SetValueByPath_ThroughArrayIntoDocument_SetsValue()
    {
        var doc = new BsonDocument
        {
            { "items", new BsonArray { new BsonDocument { { "name", "first" } } } }
        };
        BsonPath.SetValueByPath(doc, "items.0.value", new BsonInt32(99));

        var item = doc["items"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal("first", item["name"].AsString);
        Assert.Equal(99, item["value"].AsInt32);
    }

    [Fact]
    public void SetValueByPath_ThroughNestedArrays_SetsValue()
    {
        var doc = new BsonDocument
        {
            { "matrix", new BsonArray
            {
                new BsonArray { new BsonDocument { { "value", 1 } } }
            } }
        };
        BsonPath.SetValueByPath(doc, "matrix.0.0.value", new BsonInt32(99));

        var value = doc["matrix"].AsBsonArray[0].AsBsonArray[0].AsBsonDocument["value"].AsInt32;
        Assert.Equal(99, value);
    }

    [Fact]
    public void RemoveValueByPath_WithSimplePath_RemovesField()
    {
        var doc = new BsonDocument { { "name", "John" }, { "age", 30 } };
        BsonPath.RemoveValueByPath(doc, "name");
        Assert.False(doc.Contains("name"));
        Assert.True(doc.Contains("age"));
    }

    [Fact]
    public void RemoveValueByPath_WithNestedPath_RemovesField()
    {
        var doc = new BsonDocument
        {
            { "user", new BsonDocument { { "name", "John" }, { "age", 30 } } }
        };
        BsonPath.RemoveValueByPath(doc, "user.name");
        Assert.False(doc["user"].AsBsonDocument.Contains("name"));
        Assert.True(doc["user"].AsBsonDocument.Contains("age"));
    }

    [Fact]
    public void RemoveValueByPath_WithArrayIndex_RemovesElement()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b", "c" } } };
        BsonPath.RemoveValueByPath(doc, "items.1");

        var array = doc["items"].AsBsonArray;
        Assert.Equal(2, array.Count);
        Assert.Equal("a", array[0].AsString);
        Assert.Equal("c", array[1].AsString);
    }

    [Fact]
    public void RemoveValueByPath_ThroughArrayIntoDocument_RemovesField()
    {
        var doc = new BsonDocument
        {
            { "items", new BsonArray { new BsonDocument { { "name", "first" }, { "hidden", "yes" } } } }
        };
        BsonPath.RemoveValueByPath(doc, "items.0.hidden");

        var item = doc["items"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal("first", item["name"].AsString);
        Assert.False(item.Contains("hidden"));
    }

    [Fact]
    public void RemoveValueByPath_WithMissingPath_DoesNothing()
    {
        var doc = new BsonDocument { { "name", "John" } };
        BsonPath.RemoveValueByPath(doc, "missing");
        Assert.Equal("John", doc["name"].AsString);
    }

    [Fact]
    public void RemoveValueByPath_WithOutOfRangeArrayIndex_DoesNothing()
    {
        var doc = new BsonDocument { { "items", new BsonArray { "a", "b" } } };
        BsonPath.RemoveValueByPath(doc, "items.10");
        Assert.Equal(2, doc["items"].AsBsonArray.Count);
    }
}
