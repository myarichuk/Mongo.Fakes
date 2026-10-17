using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server;
using Mongo.Fakes.Server.Tests.TestSupport;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

/// <summary>
/// Duplicate-key and unique-index checks against fixture data, error reply detail,
/// maxTimeMS and reply shape.
/// </summary>
public class WriteFidelityE2ETests : IAsyncLifetime
{
    private InMemoryBaseline _baseline = null!;
    private MongoFakeServer _server = null!;
    private IMongoClient _client = null!;
    private IMongoDatabase _db = null!;
    private IMongoCollection<BsonDocument> _users = null!;

    public async Task InitializeAsync()
    {
        _baseline = new InMemoryBaseline();
        _baseline.Add("testdb", "users",
            new BsonDocument { { "_id", 1 }, { "email", "a@x" }, { "profile", new BsonDocument { { "nick", "ann" }, { "age", 30 } } } },
            new BsonDocument { { "_id", 2 }, { "email", "b@x" }, { "tags", new BsonArray { "red", "blue" } } },
            new BsonDocument { { "_id", 3 }, { "email", "c@x" }, { "tags", new BsonArray { "blue", 7 } } });

        _server = new MongoFakeServer(new BsonFileBackend(_baseline), port: 0);
        await _server.StartAsync();

        _client = new MongoClient(new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _server.Port)
        });
        _db = _client.GetDatabase("testdb");
        _users = _db.GetCollection<BsonDocument>("users");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private static FilterDefinition<BsonDocument> Id(BsonValue id) => Builders<BsonDocument>.Filter.Eq("_id", id);

    private Task CreateUniqueIndexAsync(string field, bool sparse = false, BsonDocument? partialFilter = null)
    {
        var options = new CreateIndexOptions<BsonDocument> { Unique = true, Sparse = sparse };
        if (partialFilter != null)
            options.PartialFilterExpression = partialFilter;
        return _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending(field), options));
    }

    [Fact]
    public async Task Insert_IdOfUnreadFixtureDocument_IsDuplicateKey()
    {
        // Nothing has read the collection yet, so the fixture documents have never been touched.
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 2 }, { "email", "dup@x" } }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
        Assert.Contains("index: _id_", ex.Message);
    }

    [Fact]
    public async Task Insert_NumericallyEqualId_IsDuplicateKey()
    {
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 1L } }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    [Fact]
    public async Task Insert_AfterDeletingFixtureDocument_ReusesIdAndIsVisible()
    {
        await _users.DeleteOneAsync(Id(1));
        await _users.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "email", "new@x" } });

        var doc = await _users.Find(Id(1)).SingleAsync();
        Assert.Equal("new@x", doc["email"].AsString);
        Assert.Equal(3, await _users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Update_DoesNotMatchDeletedFixtureDocument()
    {
        await _users.DeleteOneAsync(Id(2));

        var result = await _users.UpdateManyAsync(
            FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Set("seen", true));

        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(2, result.ModifiedCount);
    }

    [Fact]
    public async Task UniqueIndex_FixtureValue_BlocksInsert()
    {
        await CreateUniqueIndexAsync("email");

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "email", "c@x" } }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
        Assert.Contains("index: email_1", ex.Message);
    }

    [Fact]
    public async Task UniqueIndex_DeletedDocument_FreesItsValue()
    {
        await CreateUniqueIndexAsync("email");
        await _users.DeleteOneAsync(Id(3));

        await _users.InsertOneAsync(new BsonDocument { { "_id", 30 }, { "email", "c@x" } });

        Assert.Equal(30, (await _users.Find(Builders<BsonDocument>.Filter.Eq("email", "c@x")).SingleAsync())["_id"].AsInt32);
    }

    [Fact]
    public async Task UniqueIndex_UpdatedDocument_FreesOldValueAndClaimsNewOne()
    {
        await CreateUniqueIndexAsync("email");
        await _users.UpdateOneAsync(Id(1), Builders<BsonDocument>.Update.Set("email", "renamed@x"));

        // The old value is free again...
        await _users.InsertOneAsync(new BsonDocument { { "_id", 10 }, { "email", "a@x" } });

        // ...and the new one is taken.
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 11 }, { "email", "renamed@x" } }));
        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    [Fact]
    public async Task UniqueIndex_IsEnforcedOnUpdate()
    {
        await CreateUniqueIndexAsync("email");

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.UpdateOneAsync(Id(1), Builders<BsonDocument>.Update.Set("email", "b@x")));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
        Assert.Equal("a@x", (await _users.Find(Id(1)).SingleAsync())["email"].AsString);
    }

    [Fact]
    public async Task UniqueIndex_IsEnforcedOnUpsertAndFindAndModify()
    {
        await CreateUniqueIndexAsync("email");

        var upsert = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.UpdateOneAsync(Id(99), Builders<BsonDocument>.Update.Set("email", "a@x"), new UpdateOptions { IsUpsert = true }));
        Assert.Equal(ServerErrorCategory.DuplicateKey, upsert.WriteError.Category);

        var fam = await Assert.ThrowsAsync<MongoCommandException>(() =>
            _users.FindOneAndUpdateAsync(Id(2), Builders<BsonDocument>.Update.Set("email", "a@x")));
        Assert.Equal(11000, fam.Code);
        Assert.Equal(new BsonDocument("email", 1), fam.Result["keyPattern"]);
        Assert.Equal(new BsonDocument("email", "a@x"), fam.Result["keyValue"]);
    }

    [Fact]
    public async Task UniqueIndex_SparseSkipsDocumentsWithoutTheField()
    {
        await CreateUniqueIndexAsync("phone", sparse: true);

        await _users.InsertOneAsync(new BsonDocument { { "_id", 20 } });
        await _users.InsertOneAsync(new BsonDocument { { "_id", 21 } });
        await _users.InsertOneAsync(new BsonDocument { { "_id", 22 }, { "phone", "555" } });

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 23 }, { "phone", "555" } }));
    }

    [Fact]
    public async Task UniqueIndex_NonSparseTreatsMissingFieldAsNull()
    {
        // No fixture document has "phone", so they all share the null key and the build must fail.
        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => CreateUniqueIndexAsync("phone"));
        Assert.Equal(11000, ex.Code);

        var names = (await _users.Indexes.ListAsync()).ToList().Select(i => i["name"].AsString);
        Assert.DoesNotContain("phone_1", names);
    }

    [Fact]
    public async Task UniqueIndex_PartialFilterOnlyCoversMatchingDocuments()
    {
        await CreateUniqueIndexAsync("code", partialFilter: new BsonDocument("active", true));

        await _users.InsertOneAsync(new BsonDocument { { "_id", 40 }, { "code", "X" }, { "active", false } });
        await _users.InsertOneAsync(new BsonDocument { { "_id", 41 }, { "code", "X" }, { "active", true } });

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 42 }, { "code", "X" }, { "active", true } }));

        var index = (await _users.Indexes.ListAsync()).ToList().Single(i => i["name"] == "code_1");
        Assert.Equal(new BsonDocument("active", true), index["partialFilterExpression"]);
    }

    [Fact]
    public async Task UniqueIndex_MultikeyAndDottedPaths()
    {
        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("profile.nick"),
            new CreateIndexOptions { Unique = true, Sparse = true }));

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 50 }, { "profile", new BsonDocument("nick", "ann") } }));

        await _users.DeleteManyAsync(Builders<BsonDocument>.Filter.In("_id", new[] { 2, 3 }));
        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tags"),
            new CreateIndexOptions { Unique = true, Sparse = true }));

        // One document may repeat its own value; another may not reuse any element.
        await _users.InsertOneAsync(new BsonDocument { { "_id", 51 }, { "tags", new BsonArray { "x", "x" } } });
        await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.InsertOneAsync(new BsonDocument { { "_id", 52 }, { "tags", new BsonArray { "y", "x" } } }));
    }

    [Fact]
    public async Task CreateUniqueIndex_OnExistingDuplicates_Fails()
    {
        await _users.InsertOneAsync(new BsonDocument { { "_id", 60 }, { "email", "a@x" } });

        var ex = await Assert.ThrowsAsync<MongoCommandException>(() => CreateUniqueIndexAsync("email"));
        Assert.Equal(11000, ex.Code);
    }

    [Fact]
    public async Task WriteError_CarriesKeyPatternAndKeyValue()
    {
        var reply = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "insert", "users" },
            { "documents", new BsonArray { new BsonDocument { { "_id", 1 } } } }
        });

        var error = reply["writeErrors"][0].AsBsonDocument;
        Assert.Equal(11000, error["code"].AsInt32);
        Assert.Equal(new BsonDocument("_id", 1), error["keyPattern"]);
        Assert.Equal(new BsonDocument("_id", 1), error["keyValue"]);
        Assert.Equal("ok", reply.GetElement(reply.ElementCount - 1).Name);
    }

    [Fact]
    public async Task Replace_ChangingId_IsImmutableFieldError()
    {
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.ReplaceOneAsync(Id(1), new BsonDocument { { "_id", 100 }, { "email", "z@x" } }));

        Assert.Equal(66, ex.WriteError.Code);
    }

    [Fact]
    public async Task SetId_ToNewValue_IsImmutableField_ButSameValueIsAllowed()
    {
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _users.UpdateOneAsync(Id(1), Builders<BsonDocument>.Update.Set("_id", 100)));
        Assert.Equal(66, ex.WriteError.Code);

        var result = await _users.UpdateOneAsync(Id(1), Builders<BsonDocument>.Update.Set("_id", 1).Set("email", "same@x"));
        Assert.Equal(1, result.ModifiedCount);
    }

    [Fact]
    public async Task SuccessReply_HasOkLast()
    {
        var ping = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        var find = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("find", "users"));

        Assert.Equal("ok", ping.GetElement(ping.ElementCount - 1).Name);
        Assert.Equal("ok", find.GetElement(find.ElementCount - 1).Name);
        Assert.Equal("cursor", find.GetElement(0).Name);
    }

    // Sent as a raw command: driver 3.x doesn't send maxTimeMS for FindOptions.MaxTime.
    [Fact]
    public async Task MaxTimeMS_Exceeded_IsMaxTimeMSExpired()
    {
        _baseline.Add("testdb", "slow", new BsonDocument("_id", 1));
        _baseline.Delay = TimeSpan.FromMilliseconds(300);

        var ex = await Assert.ThrowsAsync<MongoExecutionTimeoutException>(() =>
            _db.RunCommandAsync<BsonDocument>(new BsonDocument { { "find", "slow" }, { "maxTimeMS", 50 } }));

        Assert.Equal(50, ex.Code);
    }

    [Fact]
    public async Task MaxTimeMS_NotExceeded_Succeeds()
    {
        var reply = await _db.RunCommandAsync<BsonDocument>(new BsonDocument { { "find", "users" }, { "maxTimeMS", 30000 } });

        Assert.Equal(3, reply["cursor"]["firstBatch"].AsBsonArray.Count);
    }

    [Fact]
    public async Task Distinct_UnwindsArraysAndKeepsTypes()
    {
        var values = await (await _users.DistinctAsync<BsonValue>("tags", FilterDefinition<BsonDocument>.Empty)).ToListAsync();

        Assert.Equal(3, values.Count);
        Assert.Contains(new BsonString("red"), values);
        Assert.Contains(new BsonString("blue"), values);
        Assert.Contains(new BsonInt32(7), values);
    }

    [Fact]
    public async Task ExclusionProjection_OnDottedPath_DoesNotChangeStoredDocument()
    {
        var projected = await _users.Find(Id(1)).Project(Builders<BsonDocument>.Projection.Exclude("profile.age")).SingleAsync();
        Assert.False(projected["profile"].AsBsonDocument.Contains("age"));

        var stored = await _users.Find(Id(1)).SingleAsync();
        Assert.Equal(30, stored["profile"]["age"].AsInt32);
    }

    [Fact]
    public async Task UnacknowledgedWrite_IsAppliedWithoutReply()
    {
        var unacknowledged = _users.WithWriteConcern(WriteConcern.Unacknowledged);
        await unacknowledged.InsertOneAsync(new BsonDocument { { "_id", 70 } });
        // A duplicate sent with moreToCome must not get a reply, or the driver would misread the next one.
        await unacknowledged.InsertOneAsync(new BsonDocument { { "_id", 70 } });

        Assert.Equal(4, await _users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Replies_OfVaryingSize_OnOneConnection()
    {
        var big = Enumerable.Range(1000, 3000)
            .Select(i => new BsonDocument { { "_id", i }, { "payload", new string('x', 200) } });
        await _users.InsertManyAsync(big);

        for (int round = 0; round < 3; round++)
        {
            Assert.Equal(3003, (await _users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).Count);
            Assert.Single(await _users.Find(Id(1)).ToListAsync());
        }
    }
}
