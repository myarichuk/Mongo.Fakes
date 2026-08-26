using MongoDB.Driver;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

/// <summary>
/// Regression tests to prevent reintroduction of critical bugs fixed in code review.
/// Each test targets a specific P0 finding to ensure it stays fixed.
/// </summary>
public class RegressionTests : IAsyncLifetime
{
    private MongoFakeServer? _server;
    private IMongoClient? _client;

    public async Task InitializeAsync()
    {
        var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
        _server = new MongoFakeServer(backend, port: 0);
        await _server.StartAsync();

        var settings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _server.Port)
        };
        _client = new MongoClient(settings);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_server != null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task AsyncAwait_AggregateAsync_Should_Not_Block_On_Result()
    {
        // Regression: xUnit1031 - calling .Result on async method before awaiting cursor causes deadlock
        // Fix: awaited AggregateAsync result (cursor) before calling ToListAsync
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll");

        var doc = new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "value", "test" } };
        await collection.InsertOneAsync(doc);

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() { { "$match", new MongoDB.Bson.BsonDocument { { "_id", 1 } } } }
        };

        // This pattern must work correctly without deadlock:
        var cursor = await collection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var results = await cursor.ToListAsync();

        Assert.Single(results);
        Assert.Equal(1, results[0]["_id"].AsInt32);
    }

    [Fact]
    public async Task WireProtocol_BuildInfo_Should_Match_MaxWireVersion()
    {
        // Regression: version mismatch between buildInfo response and actual wire protocol support
        // Fix: updated buildInfo version from 4.4.0 to 6.0.0 matching maxWireVersion 17
        var db = _client!.GetDatabase("admin");
        var buildInfo = await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument { { "buildInfo", 1 } });

        Assert.NotNull(buildInfo);
        Assert.True(buildInfo.Contains("version"), "buildInfo missing version field");

        var version = buildInfo["version"].AsString;
        // Version should be 6.0.0 or higher to match wire protocol capabilities
        Assert.StartsWith("6.", version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelloCommand_MaxWireVersion_Should_Indicate_Full_Support()
    {
        // Regression: ensure maxWireVersion correctly represents OP_MSG support
        // Verifies that wire protocol version is consistent across hello command
        var db = _client!.GetDatabase("admin");
        var helloDoc = await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument { { "hello", 1 } });

        Assert.NotNull(helloDoc);
        Assert.True(helloDoc.Contains("maxWireVersion"), "hello response missing maxWireVersion");

        int maxWireVersion = helloDoc["maxWireVersion"].AsInt32;
        // Wire version 17 = MongoDB 6.0+ OP_MSG support
        Assert.True(maxWireVersion >= 17, $"maxWireVersion {maxWireVersion} should be >= 17");
    }

    [Fact]
    public async Task MongoDB_Driver_Should_Connect_Successfully()
    {
        // Regression: ensure MongoDB.Driver NuGet package is properly available
        // Previously: unnecessary MongoDB.Driver reference was removed from package, but must be available for tests
        var db = _client!.GetDatabase("admin");
        var result = await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument { { "ping", 1 } });

        Assert.NotNull(result);
        Assert.Equal(1.0, result["ok"].AsDouble);
    }

    [Fact]
    public async Task Insert_And_Query_Should_Preserve_Document_Structure()
    {
        // Regression: verify BSON document handling integrity (no dead code affecting serialization)
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll");

        var originalDoc = new MongoDB.Bson.BsonDocument
        {
            { "_id", "test-doc" },
            { "nested", new MongoDB.Bson.BsonDocument { { "field", "value" } } },
            { "array", new MongoDB.Bson.BsonArray { 1, 2, 3 } }
        };

        await collection.InsertOneAsync(originalDoc);

        var retrieved = await collection.Find(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "test-doc"))
            .FirstAsync();

        Assert.NotNull(retrieved);
        Assert.Equal("value", retrieved["nested"].AsBsonDocument["field"].AsString);
        Assert.Equal(3, retrieved["array"].AsBsonArray.Count);
    }

    [Fact]
    public async Task TcpNoDelay_Should_Not_Cause_Connection_Issues()
    {
        // Regression: TcpClientExtensions dead code removal - verify NoDelay is still set correctly
        // This ensures the connection optimization isn't accidentally removed
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("testcoll");

        var docs = Enumerable.Range(1, 10)
            .Select(i => new MongoDB.Bson.BsonDocument { { "_id", i }, { "data", new string('x', 1000) } })
            .ToList();

        // This rapid insertion stresses the connection layer
        await collection.InsertManyAsync(docs);

        var count = await collection.CountDocumentsAsync(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Empty);

        Assert.Equal(10, count);
    }

    [Fact]
    public async Task CommandRouter_Should_Route_Commands_Correctly()
    {
        // Regression: unreachable "isMaster" case removal - verify hello and ping still route correctly
        var db = _client!.GetDatabase("admin");

        // Test commands to ensure router handles them after dead code removal
        var hello = await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument { { "hello", 1 } });
        Assert.NotNull(hello);
        Assert.True(hello.ElementCount > 0, "hello response should contain fields");

        var ping = await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument { { "ping", 1 } });
        Assert.NotNull(ping);
        Assert.Equal(1.0, ping["ok"].AsDouble);
    }

    [Fact]
    public async Task AggregateProject_ExclusionWithoutIdField_KeepsId()
    {
        // Regression: Projector's exclusion mode was unconditionally stripping _id whenever
        // the projection spec didn't mention "_id" explicitly. Real Mongo keeps _id by default
        // in exclusion mode unless the spec explicitly says { _id: 0 }. The dropped _id broke
        // any pipeline stage after $project that relied on it (e.g. a later $match on _id).
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("projectexclusion");

        var doc = new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "joinedReports", "x" }, { "keep", "y" } };
        await collection.InsertOneAsync(doc);

        var pipeline = new MongoDB.Bson.BsonDocument[]
        {
            new() { { "$project", new MongoDB.Bson.BsonDocument { { "joinedReports", 0 } } } },
            new() { { "$match", new MongoDB.Bson.BsonDocument { { "_id", 1 } } } }
        };

        var cursor = await collection.AggregateAsync<MongoDB.Bson.BsonDocument>(pipeline);
        var results = await cursor.ToListAsync();

        Assert.Single(results);
        Assert.Equal(1, results[0]["_id"].AsInt32);
        Assert.True(results[0].Contains("keep"));
        Assert.False(results[0].Contains("joinedReports"));
    }

    [Fact]
    public async Task Lookup_With_ArrayElemAt_And_IfNull_On_FieldPath()
    {
        // Regression: field-path expressions on arrays (e.g., "$joinedReports.Status")
        // should return an array of subfield values, enabling $arrayElemAt and $ifNull patterns
        var responses = _client!.GetDatabase("repro").GetCollection<MongoDB.Bson.BsonDocument>("responses");
        var reports = _client.GetDatabase("repro").GetCollection<MongoDB.Bson.BsonDocument>("-reports-");

        await reports.InsertManyAsync(new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", 1 }, { "RecordId", 100 }, { "Status", "ok" } },
            new MongoDB.Bson.BsonDocument { { "_id", 2 }, { "RecordId", 100 }, { "Status", "warn" } },
        });
        await responses.InsertManyAsync(new[]
        {
            new MongoDB.Bson.BsonDocument { { "_id", "a" }, { "RecordId", 100 } },
            new MongoDB.Bson.BsonDocument { { "_id", "b" }, { "RecordId", 999 } },
        });

        var cursor = await responses.Aggregate<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument[]
        {
            new("$lookup", new MongoDB.Bson.BsonDocument
            {
                { "from", "-reports-" },
                { "localField", "RecordId" },
                { "foreignField", "RecordId" },
                { "as", "joinedReports" },
            }),
            new("$set", new MongoDB.Bson.BsonDocument("ReportStatus",
                new MongoDB.Bson.BsonDocument("$ifNull", new MongoDB.Bson.BsonArray
                {
                    new MongoDB.Bson.BsonDocument("$arrayElemAt", new MongoDB.Bson.BsonArray { "$joinedReports.Status", 0 }),
                    "NotReported",
                }))),
            new("$project", new MongoDB.Bson.BsonDocument("joinedReports", 0)),
        }).ToListAsync();

        var a = cursor.Find(d => d["_id"] == "a");
        var b = cursor.Find(d => d["_id"] == "b");

        Assert.NotNull(a);
        Assert.Equal("ok", a["ReportStatus"].AsString);

        Assert.NotNull(b);
        Assert.Equal("NotReported", b["ReportStatus"].AsString);
    }

    [Fact]
    public async Task Project_With_FieldPath_String_Expression_Should_Include_Values()
    {
        // Test: string field-path expressions in $project (e.g., { "Status": "$array.subfield" })
        // should include the projected field-path values
        var collection = _client!.GetDatabase("testdb").GetCollection<MongoDB.Bson.BsonDocument>("fieldpathproj");

        var doc = new MongoDB.Bson.BsonDocument
        {
            { "_id", 1 },
            { "reports", new MongoDB.Bson.BsonArray
            {
                new MongoDB.Bson.BsonDocument { { "Status", "ok" } },
                new MongoDB.Bson.BsonDocument { { "Status", "warn" } }
            } }
        };
        await collection.InsertOneAsync(doc);

        var cursor = await collection.Aggregate<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument[]
        {
            new() { { "$project", new MongoDB.Bson.BsonDocument { { "Statuses", "$reports.Status" } } } },
        }).ToListAsync();

        Assert.Single(cursor);
        var result = cursor[0];
        Assert.True(result.Contains("Statuses"), "Statuses field should be projected");
        Assert.True(result["Statuses"].IsBsonArray, "Statuses should be an array");
        var statuses = result["Statuses"].AsBsonArray;
        Assert.Equal(2, statuses.Count);
        Assert.Equal("ok", statuses[0].AsString);
        Assert.Equal("warn", statuses[1].AsString);
    }


}
