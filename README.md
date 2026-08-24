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

## Building

```
dotnet build
dotnet test
```

Targets `net8.0` and `net10.0`.

## License

[MIT](LICENSE)
