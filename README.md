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

## Status

Early scaffold — see [`docs/SPEC.md`](docs/SPEC.md) for the design specification and
current scope.

## Packages

| Package | Purpose |
|---|---|
| `Mongo.Fakes.Core` | Filter compiler: `BsonDocument` filter → LINQ predicate |
| `Mongo.Fakes.Server` | Wire-protocol test double server backed by fixture files |

## Usage

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

## Building

```
dotnet build
dotnet test
```

Targets `net8.0` and `net10.0`.

## License

[MIT](LICENSE)
