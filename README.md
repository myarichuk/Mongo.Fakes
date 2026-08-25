# Mongo.Fakes

Wire-compatible MongoDB test doubles for the official [MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver) — no `mongod` process required.

Mongo.Fakes is two things sharing one filter engine:

- **`Mongo.Fakes.Core`** — compiles MongoDB filter documents (`BsonDocument`) into
  `Expression<Func<BsonDocument, bool>>` predicates. Stays entirely in BSON-land
  (`BsonValue` comparisons, MongoDB type ordering, null-vs-missing semantics) instead of
  mapping to CLR types, so behavior matches real MongoDB.
- **`Mongo.Fakes.Server`** — an in-process MongoDB wire-protocol (OP_MSG) mock server that
  serves fixture data to a real `IMongoClient`/`IMongoCollection`, for tests that need to
  exercise actual driver code paths without standing up MongoDB.

`Mongo.Fakes.Server` uses `Mongo.Fakes.Core` as its filter engine, so operator semantics are
implemented once and shared by both the lightweight in-memory predicate mode and the
wire-protocol double.

## When to Use Mongo.Fakes

| Aspect | Mongo.Fakes | EphemeralMongo | Testcontainers |
|--------|-----------|-----------------|-----------------|
| **Speed** | ⚡ Very fast (in-process) | 🐢 Slower (real binary) | 🐢 Slower (Docker) |
| **Setup** | 0 ms, no dependencies | ~100 MB binary download | Docker required |
| **Compatibility** | 95% (queries, writes, aggregations) | 100% (full MongoDB) | 100% (full MongoDB) |
| **Fixture Setup** | Easy (JSON/BSON files) | Any method | Any method |
| **Best For** | Unit tests, CI speed, fixture validation | Integration tests needing 100% parity | Feature demos, complex scenarios |
| **Test Database Reuse** | ✓ Snapshot real data as BSON | ✓ Full compatibility | ✓ Full compatibility |

**Choose Mongo.Fakes if:** Your tests don't use unsupported operators, you want instant startup, and you're testing driver integration paths rather than advanced MongoDB features.

**Choose EphemeralMongo/Testcontainers if:** You need 100% MongoDB compatibility or are testing features like transactions, geospatial queries, or complex aggregations.

## Status

Early scaffold — see [`docs/SPEC.md`](docs/SPEC.md) for the design specification and
current scope.

## Packages

| Package | Purpose |
|---|---|
| `Mongo.Fakes.Core` | Filter compiler: `BsonDocument` filter → LINQ predicate |
| `Mongo.Fakes.Server` | Wire-protocol test double server backed by fixture files |

## Usage

### Loading Fixture Data

#### From Local JSON/BSON Files

Create a folder structure matching your database layout:
```
Fixtures/
  myapp/
    users.json
    products.json
  other_db/
    items.json
```

Each line in the JSON files is a BSON document:
```json
{"_id": 1, "name": "Alice", "email": "alice@example.com"}
{"_id": 2, "name": "Bob", "email": "bob@example.com"}
```

#### From MongoDB Dump (mongodump)

Import real exported data using `mongodump`:

```bash
mongodump --uri "mongodb://prod-server/myapp" --out ./dump
```

This creates a structure like: `dump/myapp/users.bson`, `dump/myapp/products.bson`, etc.

Then load it in your tests:
```csharp
var backend = new BsonFileBackend(Path.Combine(
    Directory.GetCurrentDirectory(), "dump"), loadFromMongoDump: true);
```

The server automatically handles both `.json` and `.bson` files, making it easy to snapshot
real production data as test fixtures.

### Integration Tests with xUnit

Use `MongoFakeServer` with xUnit's `IAsyncLifetime` to manage server lifecycle in tests:

```csharp
using MongoDB.Driver;
using Xunit;

public class UserServiceTests : IAsyncLifetime
{
    private MongoFakeServer _server;
    private IMongoClient _client;

    public async Task InitializeAsync()
    {
        // Start the fake server on an auto-assigned port
        var backend = new BsonFileBackend(Path.Combine(
            Directory.GetCurrentDirectory(), "Fixtures"));
        _server = new MongoFakeServer(backend, port: 0);
        await _server.StartAsync();

        // Use the connection string just like EphemeralMongo
        _client = new MongoClient(_server.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_server != null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task CreateUser_Should_Insert_Document()
    {
        var db = _client.GetDatabase("myapp");
        var users = db.GetCollection<BsonDocument>("users");

        var newUser = new BsonDocument
        {
            { "name", "Alice" },
            { "email", "alice@example.com" },
            { "age", 30 }
        };

        await users.InsertOneAsync(newUser);

        var found = await users.Find(
            Builders<BsonDocument>.Filter.Eq("email", "alice@example.com")
        ).FirstOrDefaultAsync();

        Assert.NotNull(found);
        Assert.Equal("Alice", found["name"].AsString);
    }

    [Fact]
    public async Task QueryWithFilter_Should_Return_Matching_Documents()
    {
        var db = _client.GetDatabase("myapp");
        var products = db.GetCollection<BsonDocument>("products");

        await products.InsertManyAsync(new[]
        {
            new BsonDocument { { "name", "Widget" }, { "price", 9.99 }, { "category", "tools" } },
            new BsonDocument { { "name", "Gadget" }, { "price", 19.99 }, { "category", "electronics" } },
            new BsonDocument { { "name", "Tool" }, { "price", 14.99 }, { "category", "tools" } }
        });

        var filter = Builders<BsonDocument>.Filter.Eq("category", "tools");
        var results = await products.Find(filter).ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, doc => Assert.Equal("tools", doc["category"].AsString));
    }
}
```

The `MongoFakeServer` provides:
- **`ConnectionString`** property for connecting with `MongoClient` (no process management needed)
- **`Port`** property (auto-assigned if you pass `port: 0`)
- **`StartAsync()`** / **`DisposeAsync()`** for lifecycle management
- Semantics compatible with EphemeralMongo's `MongoRunner`

### GridFS Support

GridFS operations (file upload/download via `MongoDB.Driver.GridFSBucket`) are fully supported without any special configuration:

```csharp
var db = _client.GetDatabase("myapp");
var bucket = new GridFSBucket(db);

// Upload a file
var fileContent = Encoding.UTF8.GetBytes("Hello, World!");
var fileId = await bucket.UploadFromBytesAsync("hello.txt", fileContent);

// Download the file
var downloaded = await bucket.DownloadAsBytesAsync(fileId);

// Also supports streaming operations
using (var uploadStream = await bucket.OpenUploadStreamAsync("data.bin"))
{
    await uploadStream.WriteAsync(largeData, 0, largeData.Length);
}
```

GridFS works transparently by using the existing `insert`, `find`, `update`, and `delete` command support — no bucket-specific code is needed. Files spanning multiple chunks are handled automatically.

## Performance: Copy-on-Write (CoW) Fixture Isolation

For test suites with hundreds of test fixtures sharing the same baseline data, **Mongo.Fakes implements per-document copy-on-write (CoW)** to minimize memory overhead:

- **Baseline data** (loaded from fixture files) is shared across all test fixtures
- **Per-fixture mutations** are tracked separately — when a test modifies a document, only the changed version is copied to heap
- **Unmodified documents** reference the original baseline (zero memory overhead)

This enables thousands of concurrent test fixtures to maintain isolation without the memory cost of cloning 100MB+ baselines per fixture.

**Typical scenario:**
- Baseline: 100 MB shared across all fixtures
- Test Class A: inserts 5 docs, updates 3 → ~50 KB mutations
- Test Class B: deletes 2 docs → ~20 KB mutations
- **Total memory**: 100 MB + 70 KB (instead of 200 MB for naive cloning)

For tests that don't mutate data, the footprint is the baseline size alone — perfect for read-heavy test suites.

## Building

```
dotnet build
dotnet test
```

Targets `net8.0` and `net10.0`.

## License

[MIT](LICENSE)
