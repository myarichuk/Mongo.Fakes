using MongoDB.Bson;
using Mongo.Fakes.Server;
using Xunit;

namespace Mongo.Fakes.Server.Tests;

public class DocumentSnapshotTests
{
    [Fact]
    public void Constructor_WithoutInstanceName_HasNullInstanceName()
    {
        var doc = new BsonDocument { { "_id", 1 }, { "name", "test" } };
        var snapshot = new DocumentSnapshot(doc);

        Assert.Null(snapshot.InstanceName);
        Assert.Same(doc, snapshot.Original);
        Assert.Null(snapshot.Mutated);
        Assert.False(snapshot.IsDirty);
    }

    [Fact]
    public void Constructor_WithInstanceName_StoresName()
    {
        var doc = new BsonDocument { { "_id", 1 }, { "name", "test" } };
        var snapshot = new DocumentSnapshot(doc, "user_alice");

        Assert.Equal("user_alice", snapshot.InstanceName);
    }

    [Fact]
    public void Current_WhenNotMutated_ReturnsOriginal()
    {
        var doc = new BsonDocument { { "_id", 1 } };
        var snapshot = new DocumentSnapshot(doc);

        Assert.Same(doc, snapshot.Current);
    }

    [Fact]
    public void Current_WhenMutated_ReturnsMutated()
    {
        var original = new BsonDocument { { "_id", 1 }, { "status", "active" } };
        var mutated = new BsonDocument { { "_id", 1 }, { "status", "inactive" } };
        var snapshot = new DocumentSnapshot(original);

        snapshot.Mutated = mutated;

        Assert.Same(mutated, snapshot.Current);
        Assert.Same(original, snapshot.Original);
    }

    [Fact]
    public void IsDirty_WhenMutatedIsNull_ReturnsFalse()
    {
        var doc = new BsonDocument { { "_id", 1 } };
        var snapshot = new DocumentSnapshot(doc);

        Assert.False(snapshot.IsDirty);
    }

    [Fact]
    public void IsDirty_WhenMutatedIsSet_ReturnsTrue()
    {
        var doc = new BsonDocument { { "_id", 1 } };
        var snapshot = new DocumentSnapshot(doc);

        snapshot.Mutated = new BsonDocument { { "_id", 1 }, { "changed", true } };

        Assert.True(snapshot.IsDirty);
    }

    [Fact]
    public void GetDebugInfo_WithoutInstanceName_ShowsUnnamed()
    {
        var doc = new BsonDocument { { "_id", 1 } };
        var snapshot = new DocumentSnapshot(doc);

        var info = snapshot.GetDebugInfo();

        Assert.Contains("Snapshot[unnamed]", info);
        Assert.Contains("id=1", info);
        Assert.Contains("state=baseline", info);
    }

    [Fact]
    public void GetDebugInfo_WithInstanceName_ShowsName()
    {
        var doc = new BsonDocument { { "_id", 42 } };
        var snapshot = new DocumentSnapshot(doc, "user_alice");

        var info = snapshot.GetDebugInfo();

        Assert.Contains("Snapshot[user_alice]", info);
        Assert.Contains("id=42", info);
        Assert.Contains("state=baseline", info);
    }

    [Fact]
    public void GetDebugInfo_WhenMutated_ShowsMutatedState()
    {
        var doc = new BsonDocument { { "_id", 1 } };
        var snapshot = new DocumentSnapshot(doc, "test_doc");

        snapshot.Mutated = new BsonDocument { { "_id", 1 }, { "modified", true } };

        var info = snapshot.GetDebugInfo();

        Assert.Contains("state=mutated", info);
    }

    [Fact]
    public void GetDebugInfo_WithoutIdField_ShowsUnknown()
    {
        var doc = new BsonDocument { { "name", "test" } };
        var snapshot = new DocumentSnapshot(doc, "no_id");

        var info = snapshot.GetDebugInfo();

        Assert.Contains("id=unknown", info);
    }
}

public class DocumentSnapshotRegistryTests
{
    [Fact]
    public void Register_StoresSnapshot()
    {
        var registry = new DocumentSnapshotRegistry();
        var doc = new BsonDocument { { "_id", 1 } };
        var snapshot = new DocumentSnapshot(doc, "test");

        registry.Register("test", snapshot);

        Assert.Same(snapshot, registry.Get("test"));
    }

    [Fact]
    public void Register_WithNullName_Throws()
    {
        var registry = new DocumentSnapshotRegistry();
        var snapshot = new DocumentSnapshot(new BsonDocument { { "_id", 1 } });

        Assert.Throws<ArgumentException>(() => registry.Register(null!, snapshot));
    }

    [Fact]
    public void Register_WithEmptyName_Throws()
    {
        var registry = new DocumentSnapshotRegistry();
        var snapshot = new DocumentSnapshot(new BsonDocument { { "_id", 1 } });

        Assert.Throws<ArgumentException>(() => registry.Register("", snapshot));
    }

    [Fact]
    public void Register_WithNullSnapshot_Throws()
    {
        var registry = new DocumentSnapshotRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register("test", null!));
    }

    [Fact]
    public void Get_WithNonexistentName_ReturnsNull()
    {
        var registry = new DocumentSnapshotRegistry();

        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void GetDirty_WithNoSnapshots_ReturnsEmpty()
    {
        var registry = new DocumentSnapshotRegistry();

        var dirty = registry.GetDirty();

        Assert.Empty(dirty);
    }

    [Fact]
    public void GetDirty_WithOnlyCleanSnapshots_ReturnsEmpty()
    {
        var registry = new DocumentSnapshotRegistry();
        registry.Register("clean1", new DocumentSnapshot(new BsonDocument { { "_id", 1 } }));
        registry.Register("clean2", new DocumentSnapshot(new BsonDocument { { "_id", 2 } }));

        var dirty = registry.GetDirty();

        Assert.Empty(dirty);
    }

    [Fact]
    public void GetDirty_WithMixedSnapshots_ReturnsDirtyOnly()
    {
        var registry = new DocumentSnapshotRegistry();
        var clean = new DocumentSnapshot(new BsonDocument { { "_id", 1 } }, "clean");
        var dirty1 = new DocumentSnapshot(new BsonDocument { { "_id", 2 } }, "dirty1");
        var dirty2 = new DocumentSnapshot(new BsonDocument { { "_id", 3 } }, "dirty2");

        registry.Register("clean", clean);
        registry.Register("dirty1", dirty1);
        registry.Register("dirty2", dirty2);

        dirty1.Mutated = new BsonDocument { { "_id", 2 }, { "modified", true } };
        dirty2.Mutated = new BsonDocument { { "_id", 3 }, { "modified", true } };

        var result = registry.GetDirty().ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(dirty1, result);
        Assert.Contains(dirty2, result);
        Assert.DoesNotContain(clean, result);
    }

    [Fact]
    public void GetSummary_WithNoSnapshots_ReturnsEmptyMessage()
    {
        var registry = new DocumentSnapshotRegistry();

        var summary = registry.GetSummary();

        Assert.Equal("(empty registry)", summary);
    }

    [Fact]
    public void GetSummary_WithSnapshots_ListsAll()
    {
        var registry = new DocumentSnapshotRegistry();
        var snapshot1 = new DocumentSnapshot(new BsonDocument { { "_id", 1 } }, "user_alice");
        var snapshot2 = new DocumentSnapshot(new BsonDocument { { "_id", 2 } }, "user_bob");

        registry.Register("alice", snapshot1);
        registry.Register("bob", snapshot2);

        var summary = registry.GetSummary();

        Assert.Contains("Snapshot[user_alice]", summary);
        Assert.Contains("Snapshot[user_bob]", summary);
        Assert.Contains("id=1", summary);
        Assert.Contains("id=2", summary);
    }

    [Fact]
    public void GetSummary_WithDirtySnapshots_ShowsMutatedState()
    {
        var registry = new DocumentSnapshotRegistry();
        var clean = new DocumentSnapshot(new BsonDocument { { "_id", 1 } }, "clean");
        var dirty = new DocumentSnapshot(new BsonDocument { { "_id", 2 } }, "dirty");

        dirty.Mutated = new BsonDocument { { "_id", 2 }, { "modified", true } };

        registry.Register("clean", clean);
        registry.Register("dirty", dirty);

        var summary = registry.GetSummary();

        Assert.Contains("state=baseline", summary);
        Assert.Contains("state=mutated", summary);
    }
}
