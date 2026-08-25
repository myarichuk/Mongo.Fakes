# Mongo.Fakes: Specification

**Version:** 2.2 (CoW Fixture Isolation + Core filter compiler + Server wire-protocol double)
**Status:** Specification
**Last Updated:** August 25, 2026
**Author:** Michael Yarichuk

---

## Table of Contents

1. [Overview](#overview)
2. [Scope & Constraints](#scope--constraints)
3. [Architecture](#architecture)
4. [Mongo.Fakes.Core: Filter Compilation](#mongofakescore-filter-compilation)
5. [Operator Reference](#operator-reference)
6. [Type System](#type-system)
7. [Mongo.Fakes.Server: Wire Protocol](#mongofakesserver-wire-protocol)
8. [Query Execution (Server)](#query-execution-server)
9. [Data Loading](#data-loading)
10. [Test Integration](#test-integration)
11. [Command Reference](#command-reference)
12. [Error Handling](#error-handling)
13. [Performance Requirements](#performance-requirements)
14. [Testing Strategy](#testing-strategy)
15. [Prior Art: Porting from MongoZen](#prior-art-porting-from-mongozen)
16. [Document History](#document-history)

---

## Overview

### Purpose

**Mongo.Fakes** provides wire-compatible MongoDB test doubles for the official C# driver,
built around a single, shared, BSON-native filter-compilation engine.

The repo is two packages sharing one engine:

- **`Mongo.Fakes.Core`** compiles MongoDB filter syntax to LINQ
  `Expression<Func<BsonDocument, bool>>` predicates. Execute compiled expressions against
  in-memory `BsonDocument` collections without requiring a MongoDB server or CLR type
  mapping.
- **`Mongo.Fakes.Server`** is a lightweight, in-process MongoDB wire protocol (OP_MSG) mock
  server. It reads static BSON/JSON fixture files and serves them to the official
  `IMongoClient`/`IMongoCollection` driver API, so tests exercise real driver code paths
  without a `mongod` process. Its `$match`/`find` filtering is implemented by compiling
  filters through `Mongo.Fakes.Core` — it does not maintain its own copy of operator
  semantics.

### Core Insight

**Stay in BSON land.** Don't map to CLR types. `BsonValue.CompareTo()`, `BsonType`,
`BsonArray` semantics are MongoDB-correct by design. LINQ-to-Objects evaluates the
compiled expressions with correct null/array/type handling.

**One filter engine, two consumers.** Both the pure in-memory predicate mode
(`Mongo.Fakes.Core` used directly) and the wire-protocol double
(`Mongo.Fakes.Server`) need identical MongoDB filter semantics. Rather than
maintaining two implementations that can drift, `Mongo.Fakes.Server`'s query executor
calls `Mongo.Fakes.Core.FilterCompiler.Compile(filter)` for all `$match`/`find` filtering.
New operators are implemented once, in `Mongo.Fakes.Core`, and both consumers get them
for free.

### Use Cases

1. **TestFixtures:** Mock MongoDB server queries against fixture data
2. **In-memory filtering:** `collection.Where(compiledFilter).ToList()` against any
   in-memory `BsonDocument` collection
3. **Validation:** Validate filter syntax before sending it to real MongoDB
4. **Driver-level integration tests:** Run tests against a real `IMongoClient` without a
   `mongod` process (via `Mongo.Fakes.Server`)

### Design Philosophy

- **BSON-native:** All comparisons use `BsonValue` semantics, not CLR
- **Expression-based:** Compile once, execute many times
- **Single source of truth for filter semantics:** `Mongo.Fakes.Core` is the only place
  operator behavior is implemented; `Mongo.Fakes.Server` consumes it
- **Explicit over implicit:** Array unwinding is deliberate, not hidden
- **Fail loud:** Unsupported operators throw `NotSupportedException` immediately
- **Correctness over completeness:** 95% coverage of operators; 100% semantic fidelity on
  what's implemented
- **Wire protocol fidelity:** Response format must be compatible with the official
  MongoDB C# driver

---

## Scope & Constraints

### In Scope (MVP)

| Operator | Rationale | Notes |
|----------|-----------|-------|
| `$eq` | Implicit match operator; foundation | Works with arrays (implicit unwinding) |
| `$ne` | Common negation | |
| `$gt`, `$gte`, `$lt`, `$lte` | Range queries | Uses BsonValue type ordering |
| `$in` | Membership test | Array of mixed types OK |
| `$nin` | Negated membership | |
| `$exists` | Field presence | Null vs missing distinction |
| `$and` | Logical AND (implicit + explicit) | Implicit: multiple fields. Explicit: `{ $and: [...] }` |
| `$or` | Logical OR | `{ $or: [cond1, cond2, ...] }` |
| `$nor` | Logical NOR | `{ $nor: [cond1, cond2, ...] }` |
| `$not` | Logical NOT | `{ field: { $not: { $gt: 5 } } }` |
| `$type` | BSON type check | Uses BsonType enum + aliases |
| `$regex` | Pattern matching | Basic PCRE subset; flags: i, m, s, x |
| `$elemMatch` | Array element matching | Both scalar and document forms |
| `$all` | Array contains all | Ported from MongoZen prior art — see [Prior Art](#prior-art-porting-from-mongozen); low cost to include given an existing translator |
| Dot notation | Nested field access | `a.b.c` returns first match across arrays |
| Array fields | Implicit unwinding | `{ tags: "admin" }` matches if "admin" in array OR is value |

Server-only, on top of the shared filter engine:

| Feature | Notes |
|---|---|
| OP_MSG (modern handshake) | MongoDB 3.6+ standard; covers all current drivers |
| OP_QUERY (legacy) | Passthrough forwarding, not executed |
| `find`, `aggregate`, `countDocuments`, `insert`, `update`, `delete` | In-memory only; no persistence |
| Projection (`$project` equivalent) | Field inclusion/exclusion |
| Sorting, `$skip`, `$limit` | Result ordering/slicing |
| `$match`, `$project`, `$sort`, `$skip`, `$limit`, `$group`, `$unwind`, `$setWindowFields` | Aggregation pipeline stages; `$text` search supported at top level of filter |
| GridFS (`MongoDB.Driver.GridFSBucket`) | Supported transparently via existing `insert`, `find`, `update`, `delete`, and `createIndexes` commands; `listIndexes` returns empty index list. No bucket-specific server code required. |
| SCRAM-SHA-256 auth (optional) | `MongoFakeServer(backend, username:, password:)`; requires the driver to authenticate with exactly that credential — the server verifies a real SCRAM proof, it cannot accept an arbitrary password. Data commands are gated; handshake/admin commands are not. |

### Out of Scope (Explicit)

| Feature | Rationale |
|---------|-----------|
| `$where` | JavaScript eval; security/correctness nightmare |
| `$geoWithin`, `$near` | Geospatial; specialized |
| `$size` | Array length check; low priority |
| `$mod` | Modulo; low priority |
| `$regex` flags beyond i/m/s/x | PCRE subset only |
| `$jsonSchema` | Schema validation; separate concern |
| `$bits*` | Bitwise; low priority |
| Transactions / ACID | Not applicable to a single-threaded in-memory double |
| Secondary-index query planning | Fixtures are small; linear scan acceptable. Single text-index support now exists for `$text` search. |
| Replication / sharding | Not relevant for single-node test doubles |
| Authorization (roles/privileges) | Not relevant for a single-fixture test double |
| Cursor tailing / change streams | Not relevant for test scenarios |
| Complex aggregation stages (`$facet`, `$bucket`, `$redact`, ...) | Add incrementally if needed |

### Non-Goals

1. **Full MongoDB compatibility:** Aim for 95% of real-world queries, not 100%
2. **Aggregation beyond `$match`-adjacent stages:** listed stages only; add more
   incrementally
3. **Write operations with full semantics:** in-memory only, no persistence, no
   `$set`-style partial updates in v1 (full-document replacement only)
4. **Not a MongoDB reimplementation, not production-grade, not a learning tool**

### Limitations

#### `$text` Search

- **Recognized only at top level:** `$text` is recognized only as a top-level filter key, not inside `$and`/`$or` conditions
- **Single text index per collection:** Only one text index is permitted per collection; attempting to create a second one will error
- **Case-insensitive, whitespace-split terms:** Search tokens are split on whitespace and matched case-insensitively against document text
- **OR semantics:** Multiple search terms are OR'd (document matches if any term is found)
- **Scoring:** Text score is computed as the sum of term occurrence counts across indexed fields; exact weighting differs from MongoDB but is monotonic and deterministic

#### `$setWindowFields`

- **Output order:** Results are returned in partition order (as partitioned), with documents within each partition in sorted order (if `sortBy` specified) or input order (if not); this differs from MongoDB's undefined output order but is deterministic for testing
- **Window functions supported:** `$documentNumber`, `$rank`, `$sum`, `$avg`, `$min`, `$max`, `$first`, `$last`
- **Partition and sort:** `partitionBy` and `sortBy` follow MongoDB semantics; `partitionBy` omitted defaults to single partition; `sortBy` omitted means input order within partition
- **Window bounds:** `documents: [lower, upper]` with integer offsets, `"unbounded"`, or `"current"`; default window is running (0 to current) if `sortBy` present, else whole partition

---

## Architecture

### Component Overview

```
┌────────────────────────────────────────────────────────────────────────┐
│                              Mongo.Fakes                                │
├────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────┐   │
│  │  Mongo.Fakes.Core                                               │   │
│  │  ┌──────────────────────────────────────────────────────────┐  │   │
│  │  │  FilterCompiler                                          │  │   │
│  │  │  - Compile(BsonDocument) → Func<BsonDocument, bool>       │  │   │
│  │  │  - CompileExpression(...) → Expression<Func<...,bool>>    │  │   │
│  │  └──────────────┬───────────────────────────────────────────┘  │   │
│  │      ┌───────────┼───────────┬────────────┐                    │   │
│  │      ▼            ▼           ▼            ▼                    │   │
│  │   ┌─────┐    ┌────────┐  ┌────────┐  ┌──────────┐             │   │
│  │   │Logic│    │Scalar  │  │Array   │  │Helpers   │             │   │
│  │   │ops  │    │ops     │  │ops     │  │/ Utils   │             │   │
│  │   │AND/ │    │$eq,$in │  │$elemM. │  │Null chk, │             │   │
│  │   │OR/  │    │$gt,... │  │$all    │  │Type chk  │             │   │
│  │   │NOT  │    │        │  │Unwind  │  │          │             │   │
│  │   └─────┘    └────────┘  └────────┘  └──────────┘             │   │
│  │      IOperatorTranslator implementations, each returning        │   │
│  │      Expression<Func<BsonDocument, bool>>                       │   │
│  └───────────────────────────────┬────────────────────────────────┘  │
│                                   │ referenced by                     │
│                                   ▼                                    │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │  Mongo.Fakes.Server                                             │  │
│  │  ┌──────────────┐        ┌──────────────────┐                  │  │
│  │  │  TCP Server  │◄───────┤  OP_MSG Parser    │                  │  │
│  │  │ (Port 27017) │        └──────────────────┘                  │  │
│  │  └──────┬───────┘                                               │  │
│  │         ▼                                                       │  │
│  │  ┌─────────────────────────────────────────────────────────┐  │  │
│  │  │  Request Router → Find / Aggregate / Insert / Count      │  │  │
│  │  └──────────────────────────┬──────────────────────────────┘  │  │
│  │                             ▼                                  │  │
│  │  ┌─────────────────────────────────────────────────────────┐  │  │
│  │  │  BsonQueryExecutor                                        │  │  │
│  │  │  • Filter evaluation → delegates to Core.FilterCompiler   │  │  │
│  │  │  • Projection, sort, skip, limit                          │  │  │
│  │  │  • Aggregation pipeline stages                            │  │  │
│  │  └──────────────────────────┬──────────────────────────────┘  │  │
│  │                             ▼                                  │  │
│  │  ┌─────────────────────────────────────────────────────────┐  │  │
│  │  │  BsonFileBackend (IMongoBackend)                          │  │  │
│  │  │  In-memory dictionary: db.collection → docs[]              │  │  │
│  │  │  Loaded from JSON/BSON files at startup                    │  │  │
│  │  └─────────────────────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
```

`Mongo.Fakes.Core` has no dependency on `Mongo.Fakes.Server` — the reference is
one-directional (`Server` → `Core`).

### Key Classes — Mongo.Fakes.Core

#### `FilterCompiler`

```csharp
namespace Mongo.Fakes.Core;

public class FilterCompiler
{
    /// Compile MongoDB filter to executable predicate
    public Func<BsonDocument, bool> Compile(BsonDocument filter)
    {
        var expr = CompileExpression(filter);
        return expr.Compile();
    }

    /// Compile to expression (for composition/debugging)
    public Expression<Func<BsonDocument, bool>> CompileExpression(BsonDocument filter)
    {
        var param = Expression.Parameter(typeof(BsonDocument), "doc");
        var body = CompileFilterBody(filter, param);
        return Expression.Lambda<Func<BsonDocument, bool>>(body, param);
    }

    private Expression CompileFilterBody(BsonDocument filter, ParameterExpression docParam)
    {
        // Iterate filter elements
        // Separate: operators ($and, $or, etc) vs field conditions
        // AND all field conditions together
        // Combine with logical operators
    }
}
```

#### `BsonValueExpression` (Static Helpers)

```csharp
public static class BsonValueExpression
{
    /// Extract field from BsonDocument, handling dot notation
    /// "a.b.c" → doc["a"]["b"]["c"]; null if missing, BsonNull if null value exists
    public static Expression GetFieldValue(ParameterExpression docParam, string fieldPath);

    /// Build null safety check
    public static Expression BuildNullCheck(Expression bsonValueExpr);

    /// Unwrap array and apply predicate to elements; scalar fields apply the predicate directly
    public static Expression UnwindArray(Expression arrayExpr, Func<Expression, Expression> elementPredicate);

    /// Type check using BsonType
    public static Expression CheckBsonType(Expression bsonValueExpr, BsonType[] expectedTypes);
}
```

#### `IOperatorTranslator`

```csharp
public interface IOperatorTranslator
{
    string Operator { get; }  // "$eq", "$in", "$regex", etc.

    /// fieldValueExpr: Expression<BsonValue>; operatorValue: value from filter (e.g. 5 for { $gt: 5 })
    Expression Translate(Expression fieldValueExpr, BsonValue operatorValue);
}
```

#### Concrete Translators

```csharp
// Scalar
public class EqOperatorTranslator : IOperatorTranslator
public class NeOperatorTranslator : IOperatorTranslator
public class GtOperatorTranslator : IOperatorTranslator
public class GteOperatorTranslator : IOperatorTranslator
public class LtOperatorTranslator : IOperatorTranslator
public class LteOperatorTranslator : IOperatorTranslator

// Collection
public class InOperatorTranslator : IOperatorTranslator
public class NinOperatorTranslator : IOperatorTranslator

// Type / pattern
public class ExistsOperatorTranslator : IOperatorTranslator
public class TypeOperatorTranslator : IOperatorTranslator
public class RegexOperatorTranslator : IOperatorTranslator

// Array
public class ElemMatchOperatorTranslator : IOperatorTranslator
public class AllOperatorTranslator : IOperatorTranslator

// Logical (special handling in FilterCompiler)
public class AndOperatorTranslator : IOperatorTranslator
public class OrOperatorTranslator : IOperatorTranslator
public class NotOperatorTranslator : IOperatorTranslator
```

---

## Mongo.Fakes.Core: Filter Compilation

### Compilation Flow

```
BsonDocument filter
    │
[1] Validate structure (throw on unknown operators early)
    │
[2] Separate logical ops ($and, $or, $not) from field conditions
    │
[3] For each field condition:
      - Extract field value from doc (dot notation)
      - Compile operators applied to that field
      - Combine operators with AND
    │
[4] Combine all field conditions with AND
    │
[5] Apply logical operators (AND, OR, NOT at top level)
    │
[6] Return Expression<Func<BsonDocument, bool>>
    │
[7] Compile() to executable Func
```

### Example: `{ status: "active", age: { $gt: 18 } }`

```csharp
// Two field conditions, implicitly AND-ed
fieldExpr_status = GetFieldValue(doc, "status")
condExpr_status  = fieldExpr_status.CompareTo("active") == 0

fieldExpr_age = GetFieldValue(doc, "age")
condExpr_age  = fieldExpr_age.CompareTo(18) > 0

combined = Expression.AndAlso(condExpr_status, condExpr_age)
predicate = combined.Compile()
// (BsonDocument doc) => doc["status"].CompareTo("active") == 0 && doc["age"].CompareTo(18) > 0
```

### Example: `{ tags: "admin" }` (Array Matching)

```csharp
fieldExpr = GetFieldValue(doc, "tags")  // could be string, array, missing

scalarMatch = fieldExpr.CompareTo("admin") == 0
arrayMatch  = fieldExpr is BsonArray
    ? fieldExpr.AsEnumerable().Any(item => item.CompareTo("admin") == 0)
    : false

combined = scalarMatch || arrayMatch
```

### Example: `{ $or: [ { status: "active" }, { status: "pending" } ] }`

```csharp
cond1 = CompileFieldCondition("status", "active")
cond2 = CompileFieldCondition("status", "pending")
combined = Expression.OrElse(cond1, cond2)
```

---

## Operator Reference

All comparisons use `BsonValue.CompareTo()`, which implements MongoDB type ordering.
`BsonValue.CompareTo` is already BSON-aware — never fall back to CLR `CompareTo`.

### Comparison Operators

#### `$eq` (Equality)

**Syntax:** `{ field: value }` or `{ field: { $eq: value } }`

- Scalar: exact match
- Array field: implicit unwinding (matches if value in array OR value == field)
- Null field: matches (equality)
- Missing field: does NOT match

```csharp
fieldValue.CompareTo(expectedValue) == 0
```

#### `$ne` (Not Equal)

**Syntax:** `{ field: { $ne: value } }`

- Missing field: matches (vacuously true)
- Null field: matches (usually)
- Array field: matches if NO element equals value

```csharp
fieldValue.CompareTo(expectedValue) != 0
```

#### `$gt`, `$gte`, `$lt`, `$lte` (Comparison)

**Syntax:** `{ field: { $gt: value } }`

- Uses MongoDB type ordering (Null < Numbers < Strings < Objects < ...)
- Null/missing field: does NOT match
- Array field: matches if ANY element satisfies condition

```csharp
fieldValue.CompareTo(expectedValue) > 0  // $gt
fieldValue.CompareTo(expectedValue) >= 0 // $gte
fieldValue.CompareTo(expectedValue) < 0  // $lt
fieldValue.CompareTo(expectedValue) <= 0 // $lte
```

### Collection Operators

#### `$in` (Membership)

**Syntax:** `{ field: { $in: [val1, val2, ...] } }`

- Array of mixed types OK: `{ $in: [ObjectId(...), 5, "string"] }`
- Scalar field: direct membership test
- Array field: ANY element in values array
- Missing field: does NOT match

```csharp
valuesArray.Contains(fieldValue)
// array field:
fieldValue.AsEnumerable().Any(item => valuesArray.Contains(item))
```

#### `$nin` (Non-membership)

**Syntax:** `{ field: { $nin: [val1, val2, ...] } }`

- Opposite of `$in`; missing field DOES match

```csharp
!valuesArray.Contains(fieldValue)
```

#### `$all` (Array Contains All)

**Syntax:** `{ field: { $all: [val1, val2, ...] } }`

- Array field: matches if every value in the `$all` array is present in the field's array
- Non-array field, single-element `$all`: treated as equality

```csharp
allValues.All(v => fieldValue.AsEnumerable().Any(item => item.CompareTo(v) == 0))
```

### Existence Operators

#### `$exists` (Field Presence)

**Syntax:** `{ field: { $exists: true|false } }`

- `true`: field exists (could be null, could be any value)
- `false`: field missing (null is NOT missing)

```csharp
private bool FieldExists(BsonDocument doc, string fieldPath)
{
    // Split fieldPath, traverse; TryGetValue at each level
    // Return false if any level missing; true if all exist
}
```

### Type Operators

#### `$type` (BSON Type Check)

**Syntax:** `{ field: { $type: "string" } }` or `{ field: { $type: ["string", "null"] } }`

BSON type names: `"null"`, `"int"`, `"long"`, `"double"`, `"decimal"`, `"string"`,
`"object"`, `"array"`, `"objectId"`, `"bool"`, `"date"`, `"regex"`, `"binary"`, `"code"`.
`"number"` is an alias matching int32/int64/double/decimal128.

```csharp
expectedBsonTypes.Contains(fieldValue.BsonType)
```

#### `$regex` (Pattern Matching)

**Syntax:** `{ field: { $regex: "pattern" } }` or `{ field: { $regex: "pattern", $options: "i" } }`

- Null/missing field: does NOT match (returns false, not error)
- Array field: matches if ANY element matches
- Options: `i` (ignore case), `m` (multiline), `s` (dotall), `x` (verbose)
- MongoDB regex is PCRE-based; .NET `Regex` uses a different flavor — not 100%
  compatible, close enough for tests

```csharp
fieldValue != null && Regex.IsMatch(fieldValue.AsString, pattern, regexOptions)
```

### Array Operators

#### `$elemMatch` (Array Element Matching)

**Syntax:** `{ field: { $elemMatch: { condition } } }`

Two forms:

```json
// Scalar elements
{ "tags": { "$elemMatch": { "$eq": "admin" } } }
// where tags: ["admin", "developer"] → matches

// Document elements
{ "users": { "$elemMatch": { "age": { "$gt": 20 }, "status": "active" } } }
// where users: [{age: 25, status: "active"}, ...] → matches
```

```csharp
private Expression CompileElemMatch(Expression arrayExpr, BsonDocument condition)
{
    if (AllOperators(condition))
    {
        // Scalar form: arrayExpr.AsEnumerable().Any(predicate)
    }
    else
    {
        // Document form: arrayExpr.AsEnumerable().Any(doc => compiledCondition(doc))
    }
}
```

### Logical Operators

#### `$and` — Implicit (multiple fields) or explicit `{ $and: [cond1, cond2, ...] }`

```csharp
cond1 && cond2 && cond3  // Expression.AndAlso
```

#### `$or` — `{ $or: [cond1, cond2, ...] }`

```csharp
cond1 || cond2 || cond3  // Expression.OrElse
```

#### `$not` — `{ field: { $not: { condition } } }`

```json
{ "age": { "$not": { "$gt": 18 } } }  // age <= 18
```

```csharp
!(condition)  // Expression.Not
```

---

## Type System

### BsonValue Comparison

```
Null < Numbers < String < Object < Array < BinaryData < ObjectId < Boolean < Date < Regex < Code < Symbol < Int32 < Timestamp < Int64 < Decimal128
```

### Array Matching Semantics

```csharp
// Query: { tags: "admin" }
// { tags: ["admin", "dev"] } → MATCH (scalar in array)
// { tags: "admin" }          → MATCH (scalar == field)
// { tags: null }             → NO MATCH
// {} (missing)               → NO MATCH

fieldValue is BsonArray arr
    ? arr.Any(item => item.CompareTo("admin") == 0)
    : fieldValue?.CompareTo("admin") == 0
```

### Null vs Missing

```csharp
// Null: field exists with null value        → { x: null }
// Missing: field doesn't exist               → { }
//
// { x: null } matches both.
// { x: { $exists: true } } matches only the null-value doc.
// { x: { $exists: false } } matches only the missing-field doc.

bool TryGetField(BsonDocument doc, string fieldPath, out BsonValue value)
{
    // true if field exists (even if value is null); value is BsonNull or actual value
    // false if field missing
}
```

---

## Mongo.Fakes.Server: Wire Protocol

### Scope

- **OP_MSG (opcode 2013):** Full support, including flag handling and multiple sections
- **OP_QUERY (opcode 2004):** Handled for driver 3.x handshake (`isMaster` + `helloOk` negotiation)
- **All other opcodes:** Connection error; not supported

### Connection Handshake

On connection, the client sends `hello`/`isMaster`. The server responds:

```csharp
new BsonDocument
{
    { "ok", 1.0 },
    { "isWritablePrimary", true },
    { "ismaster", true },
    { "helloOk", true },
    { "maxWireVersion", 17 },
    { "minWireVersion", 0 },
    { "maxBsonObjectSize", 16777216 },
    { "maxMessageSizeBytes", 48000000 },
    { "maxWriteBatchSize", 100000 },
    { "localTime", DateTime.UtcNow },
    { "logicalSessionTimeoutMinutes", 30 },
    { "connectionId", <atomic-counter> },
    { "readOnly", false }
}
```

### Response Format

All responses are OP_MSG frames containing:

```csharp
new BsonDocument
{
    { "ok", 1 }, // or 0 on error
    { "result", resultArray or resultDoc },
    // Command-specific fields
}
```

### Key Classes — Mongo.Fakes.Server

#### `MongoFakeServer`

```csharp
namespace Mongo.Fakes.Server;

public class MongoFakeServer : IAsyncDisposable
{
    private readonly IMongoBackend _backend;
    private readonly TcpListener _listener;
    private readonly int _port;
    private CancellationTokenSource _cts;

    public MongoFakeServer(IMongoBackend backend, int port = 27017);
    public Task StartAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
}
```

#### `IMongoBackend`

```csharp
public interface IMongoBackend
{
    Task<BsonDocument> ExecuteCommandAsync(string database, BsonDocument command, CancellationToken ct);

    IAsyncEnumerable<BsonDocument> ExecuteQueryAsync(
        string database, string collection, BsonDocument filter, BsonDocument projection,
        int skip, int limit, CancellationToken ct);
}
```

#### `IBaselineDataProvider`

```csharp
public interface IBaselineDataProvider
{
    /// Get all documents in a collection from baseline
    IReadOnlyList<BsonDocument> GetCollection(string database, string collection);
    
    /// Get list of database names
    IReadOnlyList<string> GetDatabases();
    
    /// Get list of collection names in a database
    IReadOnlyList<string> GetCollections(string database);
}
```

#### `FileBasedBaselineProvider`

```csharp
public class FileBasedBaselineProvider : IBaselineDataProvider
{
    // Loaded once at startup from fixture files
    private readonly Dictionary<string, Dictionary<string, List<BsonDocument>>> _databases;
    
    public FileBasedBaselineProvider(string fixtureRootFolder);
    public FileBasedBaselineProvider(string fixtureRootFolder, bool loadFromMongoDump);
}
```

Shared across all test fixtures; immutable after construction.

#### `DocumentSnapshot`

```csharp
internal class DocumentSnapshot
{
    public BsonDocument Original { get; }      // Reference to baseline
    public BsonDocument? Mutated { get; set; } // Created on first write
    
    public BsonDocument Current => Mutated ?? Original;
}
```

Per-document mutation tracking; one instance per modified document per fixture.

#### `BsonFileBackend`

```csharp
public class BsonFileBackend : IMongoBackend
{
    // Shared baseline (immutable)
    private readonly IBaselineDataProvider _baseline;
    
    // Per-fixture mutation tracking
    private readonly Dictionary<string, DocumentSnapshot> _snapshots;
    private readonly HashSet<string> _deletedIds;

    // Legacy constructor for backward compatibility
    public BsonFileBackend(string fixtureFolder);
    
    // Explicit CoW constructor
    public BsonFileBackend(IBaselineDataProvider baseline);
    
    public Task<BsonDocument> ExecuteCommandAsync(string database, BsonDocument command, CancellationToken ct);
}
```

Each fixture instance maintains its own `_snapshots` and `_deletedIds`, enabling isolation while sharing `_baseline` across fixtures.

#### `BsonQueryExecutor`

```csharp
public class BsonQueryExecutor
{
    private readonly FilterCompiler _filterCompiler; // Mongo.Fakes.Core

    public IEnumerable<BsonDocument> ExecuteFind(
        BsonDocument[] data, BsonDocument filter, BsonDocument projection, int skip, int limit)
    {
        var predicate = _filterCompiler.Compile(filter);
        var results = data.Where(predicate);
        // apply projection, skip, limit
    }

    public IEnumerable<BsonDocument> ExecuteAggregate(BsonDocument[] data, BsonArray pipeline);

    private BsonDocument ApplyProjection(BsonDocument doc, BsonDocument projection);
    private IEnumerable<BsonDocument> ApplyPipelineStage(IEnumerable<BsonDocument> data, BsonDocument stage);
}
```

`BsonQueryExecutor` has no operator-matching logic of its own — `$match` and `find`
filters are compiled via `Mongo.Fakes.Core.FilterCompiler` and run as ordinary LINQ
predicates over the loaded fixture arrays.

---

## Query Execution (Server)

#### Projection

**Inclusion mode** (`1` = include): `{ "name": 1, "email": 1 }`
**Exclusion mode** (`0` = exclude): `{ "password": 0 }`

Rules:
1. Cannot mix inclusion and exclusion (except `_id`, which can always be controlled)
2. `_id` is included by default unless explicitly excluded
3. Nested projection: `{ "user.name": 1 }` includes only `user.name`

```csharp
BsonDocument ApplyProjection(BsonDocument doc, BsonDocument projection)
{
    var isInclusive = IsInclusiveProjection(projection);
    var result = new BsonDocument();

    if (isInclusive)
    {
        foreach (var field in projection.Elements)
            if (field.Value.AsInt32 == 1)
            {
                var value = GetFieldValue(doc, field.Name);
                if (value != null) SetFieldValue(result, field.Name, value);
            }
    }
    else
    {
        result = new BsonDocument(doc);
        foreach (var field in projection.Elements)
            if (field.Value.AsInt32 == 0) RemoveFieldValue(result, field.Name);
    }

    return result;
}
```

#### Sorting

`{ "name": 1, "age": -1 }` (1 = asc, -1 = desc)

```csharp
IEnumerable<BsonDocument> ApplySort(IEnumerable<BsonDocument> data, BsonDocument sortSpec)
{
    var comparer = new BsonDocumentSortComparer(sortSpec);
    return data.OrderBy(doc => doc, comparer);
}
```

#### Aggregation Pipeline

```csharp
IEnumerable<BsonDocument> ExecuteAggregate(BsonDocument[] data, BsonArray pipeline)
{
    var result = (IEnumerable<BsonDocument>)data;

    foreach (var stageDoc in pipeline.Cast<BsonDocument>())
    {
        var stageName = stageDoc.GetElement(0).Name;
        var stageSpec = stageDoc[stageName];

        result = stageName switch
        {
            "$match"   => _filterCompiler.Compile(stageSpec.AsBsonDocument) is var pred
                            ? result.Where(pred) : result,
            "$project" => HandleProjectStage(result, stageSpec.AsBsonDocument),
            "$sort"    => HandleSortStage(result, stageSpec.AsBsonDocument),
            "$skip"    => HandleSkipStage(result, stageSpec.AsInt32),
            "$limit"   => HandleLimitStage(result, stageSpec.AsInt32),
            "$group"   => HandleGroupStage(result, stageSpec.AsBsonDocument),
            "$unwind"  => HandleUnwindStage(result, stageSpec),
            _ => throw new NotSupportedException($"Stage {stageName}")
        };
    }

    return result;
}
```

Supported stages (MVP): `$match`, `$project`, `$sort`, `$limit`, `$skip`, `$group`,
`$unwind`.

`$group` accumulators: `$sum`, `$count`, `$avg`, `$min`, `$max`, `$push`, `$first`,
`$last`.

```json
{
  "_id": "$category",
  "count": { "$sum": 1 },
  "total": { "$sum": "$price" },
  "items": { "$push": "$name" }
}
```

#### Nested Field Access (Dot Notation)

```csharp
BsonValue GetFieldValue(BsonDocument doc, string path)
{
    var parts = path.Split('.');
    BsonValue current = doc;
    foreach (var part in parts)
    {
        if (current is not BsonDocument docCurrent) return null;
        if (!docCurrent.TryGetElement(part, out var element)) return null;
        current = element.Value;
    }
    return current;
}
```

---

## Data Loading

### Copy-on-Write (CoW) Fixture Isolation

To support thousands of test fixtures sharing baseline data efficiently, **Mongo.Fakes implements per-document copy-on-write (CoW) isolation**:

#### Architecture

```
Baseline Data (shared, file-backed)
  ↓
IBaselineDataProvider (read-only interface)
  ↓
Per-Fixture BsonFileBackend Instance
  ├─ DocumentSnapshot tracking (mutations only)
  ├─ Deleted IDs set (logical deletes)
  └─ Mutations dictionary (modified documents)
```

#### How It Works

1. **Baseline Loading**: `FileBasedBaselineProvider` loads fixture files once at application startup and holds them immutable in memory.

2. **Per-Fixture Isolation**: Each test fixture creates its own `BsonFileBackend(IBaselineDataProvider)` instance pointing to the shared baseline provider.

3. **Mutation Tracking**: Each backend tracks mutations separately using `DocumentSnapshot`:
   ```csharp
   private class DocumentSnapshot
   {
       public BsonDocument Original { get; }      // Reference to baseline (immutable)
       public BsonDocument? Mutated { get; set; } // Mutation copy (created on first write)
       
       public BsonDocument Current => Mutated ?? Original;  // Return current version
   }
   ```

4. **Read Operations**: Return the "current" version of each document (original if unmutated, mutated if changed).

5. **Write Operations**: On first mutation, create a copy and store it in `Mutated`; subsequent operations modify the copy.

6. **Delete Operations**: Track deleted document IDs separately; they're filtered from reads.

7. **Teardown**: Mutations are naturally garbage-collected when the fixture's `BsonFileBackend` is disposed.

#### Memory Efficiency

**Scenario: 1000 test fixtures, 100 MB baseline**

- **Naive cloning:** 1000 × 100 MB = 100 GB ❌
- **CoW approach:** 100 MB baseline + ~1 MB mutations (per 10-20 fixture mutations) = ~101-102 MB ✅

**Per-document key:** Document identity is based on `_id` JSON representation, supporting any BSON type (ObjectId, string, integer, etc.).

#### Supported Operations

| Operation | Behavior |
|-----------|----------|
| Reads | Return current document (original or mutated) |
| Insert | Add new document to snapshot tracking |
| Update | Create mutated copy on first write |
| Delete | Mark document as deleted (filtered from reads) |
| Aggregate/Filter | Use current version of each document |

---

### Fixture File Format

**JSON (primary)** — newline-delimited JSON (JSONL), one document per line:

```json
// fixtures/testdb/users.json
{ "_id": "user1", "name": "Alice", "email": "alice@example.com", "status": "active" }
{ "_id": "user2", "name": "Bob", "email": "bob@example.com", "status": "inactive" }
```

**BSON (optional)** — for large/complex documents, binary BSON documents concatenated
without framing:

```
// fixtures/testdb/orders.bson
[BSON doc 1][BSON doc 2][BSON doc 3]...
```

### Directory Structure

```
fixtures/
├── testdb/
│   ├── users.json
│   ├── products.json
│   ├── orders.bson
│   └── _metadata.json (optional)
└── statsdb/
    ├── metrics.json
    └── logs.json
```

Convention: directory = database name, file = collection name (without extension).

### Loading Strategy

```csharp
public class BsonFileBackend : IMongoBackend
{
    private readonly Dictionary<string, Dictionary<string, BsonDocument[]>> _databases;

    public BsonFileBackend(string fixtureRootFolder)
    {
        _databases = LoadAllFixtures(fixtureRootFolder);
    }

    private BsonDocument[] LoadJsonFile(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(BsonDocument.Parse)
            .ToArray();

    private BsonDocument[] LoadBsonFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var docs = new List<BsonDocument>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size = BitConverter.ToInt32(bytes, offset);
            var docBytes = new byte[size];
            Array.Copy(bytes, offset, docBytes, 0, size);
            docs.Add(BsonDocument.FromByteArray(docBytes));
            offset += size;
        }
        return docs.ToArray();
    }
}
```

Fixtures are hand-curated or exported from staging, and version-controlled alongside the
code that changes query expectations.

---

## Test Integration

### xUnit

```csharp
public class MongoFakeFixture : IAsyncLifetime
{
    private MongoFakeServer _server;
    public IMongoClient Client { get; private set; }

    public async Task InitializeAsync()
    {
        var backend = new BsonFileBackend("./fixtures");
        _server = new MongoFakeServer(backend, port: 27017);
        await _server.StartAsync(CancellationToken.None);
        Client = new MongoClient("mongodb://localhost:27017");
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();
}

public class UserRepositoryTests : IClassFixture<MongoFakeFixture>
{
    private readonly MongoFakeFixture _fixture;
    public UserRepositoryTests(MongoFakeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FindActiveUsers_ReturnsOnlyActive()
    {
        var users = _fixture.Client.GetDatabase("testdb").GetCollection<BsonDocument>("users");
        var results = await users.Find(new BsonDocument { { "status", "active" } }).ToListAsync();

        Assert.NotEmpty(results);
        Assert.All(results, u => Assert.Equal("active", u["status"].AsString));
    }
}
```

### Standalone

```csharp
public static class MongoFakeLoader
{
    public static async Task<MongoFakeServer> StartAsync(string fixtureFolder, int port = 27017)
    {
        var backend = new BsonFileBackend(fixtureFolder);
        var server = new MongoFakeServer(backend, port);
        await server.StartAsync(CancellationToken.None);
        return server;
    }
}
```

---

## Command Reference

### `find`

```csharp
// Request
{ "find": "collection", "filter": {...}, "projection": {...}, "sort": {...}, "skip": 0, "limit": 0 }

// Response
{ "ok": 1, "cursor": { "id": 0, "ns": "db.collection", "firstBatch": [ {...}, ... ] } }
```

`id` is always `0` (no cursor management); `firstBatch` contains all results.

### `aggregate`

```csharp
{ "aggregate": "collection", "pipeline": [ { $stage1 }, ... ] }
// → { "ok": 1, "cursor": { "id": 0, "ns": "db.collection", "firstBatch": [...] } }
```

### `count`

The MongoDB server command is `count`, not `countDocuments`. The driver's `CountDocumentsAsync` translates internally to an `aggregate` pipeline with `$match` + `$group`.

```csharp
{ "count": "collection", "query": {...} }  // → { "ok": 1.0, "n": 42 }
```

### `insert`

```csharp
{ "insert": "collection", "documents": [ {...}, {...} ] }  // → { "ok": 1, "n": 2 }
```

In-memory only; not persisted to fixture files.

### `update`

```csharp
{ "update": "collection", "updates": [ { "q": {...}, "u": {...} } ] }
// → { "ok": 1.0, "n": 1, "nModified": 1 }
```

Note: `n` (not `nMatched`) is the count of matched + upserted documents. `nModified` is the count of actually modified documents.

Full document replacement, or operator-style updates via `$set`, `$unset`, `$inc`, `$mul`,
`$min`, `$max`, `$push` (with `$each`/`$sort`/`$slice`/`$position`), `$pull`, `$pullAll`,
`$pop`, `$addToSet` (with `$each`), `$rename`, `$currentDate`, `$setOnInsert`.

### `delete`

```csharp
{ "delete": "collection", "deletes": [ { "q": {...}, "limit": 0 } ] }  // → { "ok": 1, "n": 2 }
```

### `hello` / `isMaster`

```csharp
{ "hello": 1 }  // → { "ok": 1.0, "isWritablePrimary": true, "ismaster": true, 
                //      "helloOk": true, "maxWireVersion": 17, ... }
```

See handshake response above for full details.

---

## Error Handling

### Compile-Time vs Runtime (Core)

**Compile-time** (throw during `FilterCompiler.Compile(filter)`):
- Unknown operators (e.g. `$foobar`) → `NotSupportedException`
- Invalid operator value shape (e.g. `{ $in: 5 }` not an array) → `ArgumentException`
- Unsupported regex flags (e.g. `$options: "z"`) → `NotSupportedException`
- Invalid field path (e.g. `{ "a..b": 5 }`) → `ArgumentException`

**Runtime** (executing the compiled predicate): no throws; MongoDB null/missing/type
semantics apply. `BsonValue.CompareTo` already handles cross-type comparisons correctly.

### Server Error Categories

| Category | Status | Behavior |
|----------|--------|----------|
| Command parse error | 400 | Error doc with `ok: 0`; close connection |
| Unknown command | 400 | Error doc; connection stays open |
| Collection not found | 200 | Empty result set (MongoDB behavior) |
| Invalid filter/projection | 400 | Error doc describing the issue |
| Unsupported operator | 400 | Error doc listing supported operators |
| Type mismatch in comparison | 200 | Skip document (safe behavior) |

```csharp
{
  "ok": 0,
  "errmsg": "Descriptive error message",
  "code": 10001,
  "codeName": "OperationFailed"
}
```

Connection handling: wire-protocol violations log and close the connection; a 5s timeout
returns a timeout error and closes; client disconnects are logged and resources cleaned up.

---

## Performance Requirements

| Operation | Target | Notes |
|-----------|--------|-------|
| Server startup | <50ms | Load fixtures into memory |
| Simple find (1000 docs) | <5ms | Linear scan + compiled predicate |
| Aggregate with `$match` (1000 docs) | <10ms | Pipeline execution |
| Sorted find (1000 docs) | <20ms | LINQ `OrderBy` |
| Complex query (5 stages, 1000 docs) | <50ms | Full pipeline |

Scalability assumptions: up to 100k documents per collection, 1-5 concurrent clients
(test thread pool), ~1MB memory per 10k documents. All fixtures load at startup; query
results materialize in-memory; no caching (queries recompute every request for
determinism).

---

## Testing Strategy

### Unit Tests — `Mongo.Fakes.Core.Tests`

- `FilterCompiler` behavior: simple equality, comparison operators, array unwinding,
  null-vs-missing distinction, `$or`/`$and`/`$not` composition, unknown-operator throwing
  at compile time.
- Per-operator translator tests (e.g. `$in` with mixed BSON types).

### Integration Tests — Against Real MongoDB

Run the same filter through the compiled `Mongo.Fakes.Core` predicate and through a real
`mongod` (see [ephemeral MongoDB testing](#testing-strategy) in `CONTRIBUTING.md`),
asserting identical result sets. This is the actual correctness backstop for filter
semantics — not just isolated unit assertions.

```csharp
[Test]
public void CompileFilter_MatchesMongoResults()
{
    var filter = BsonDocument.Parse("{ status: 'active', tags: 'admin' }");

    var mongoResults = _collection.Find(filter).ToList();

    var allDocs = _collection.Find(Builders<BsonDocument>.Filter.Empty).ToList();
    var predicate = _compiler.Compile(filter);
    var compiledResults = allDocs.Where(predicate).ToList();

    Assert.AreEqual(mongoResults.Count, compiledResults.Count);
}
```

### Fuzz Testing

Generate random filters, run them against both real MongoDB and the compiled predicate,
and assert matching result counts; `NotSupportedException` for intentionally-unsupported
operators is an expected outcome, not a failure.

### Server Tests — `Mongo.Fakes.Server.Tests`

- `BsonQueryExecutor`: filter evaluation via `Mongo.Fakes.Core`, projection modes
  (inclusive/exclusive), multi-stage aggregation.
- `BsonFileBackend`: JSON/BSON fixture loading, multiple databases, malformed-input
  handling.
- Wire protocol: handshake, command parsing, response serialization, error responses.
- End-to-end, driver-level: real `IMongoClient` against `MongoFakeServer`, covering `find`
  and `aggregate` round trips.

### Coverage Goals

- `Mongo.Fakes.Core`: 85%+ — all operators, all logical combinators, null/missing/array
  edge cases
- `Mongo.Fakes.Server` query executor: 85%+; data store: 90%+
- Wire protocol: 80%+
- Integration: representative end-to-end scenarios (find, aggregate, multi-stage,
  concurrent clients)

---

## Prior Art: Porting from MongoZen

The `Mongo.Fakes.Core` operator-translator design is not written from scratch — it
adapts an existing implementation from
[`myarichuk/MongoZen`](https://github.com/myarichuk/MongoZen), branch
`perf/optimize-all-operator-1621669543745644030`, path `src/MongoZen/FilterUtils`:

- `FilterToLinqTranslator` / `FilterToLinqTranslatorFactory` / `IFilterToLinqTranslator`,
  `FilterOperatorHandlerDiscovery`, `FilterExtensions` — the compiler/dispatch layer,
  analogous to `Mongo.Fakes.Core.FilterCompiler` and its operator registration.
- `FilterUtils/ExpressionTranslators/*` — one class per operator implementing
  `IFilterElementTranslator` (via `FilterElementTranslatorBase`): `Eq`, `NEq`, `Gt`,
  `Gte`, `Lt`, `Lte`, `In`, `NIn`, `Exists`, `Type`, `Regex`, `All`, `ElemMatch`,
  `BinaryOperator` — directly analogous to `Mongo.Fakes.Core`'s `IOperatorTranslator`
  implementations, and already covering `$all` (hence its inclusion in MVP scope above).

Porting is not a verbatim copy: the source translators target a different expression
shape than this spec's BsonValue-first, `Expression<Func<BsonDocument, bool>>` design, so
each translator is adapted rather than copy-pasted. The MongoZen repo/branch is vendored
as a temporary git submodule at `vendor/MongoZen` during porting and removed once porting
is complete — it is a one-time reference, not a runtime or build dependency.

---

## Document History

| Version | Date | Author | Change |
|---------|------|--------|--------|
| 2.2 | 2026-08-25 | Michael Yarichuk | Implemented per-document CoW isolation for efficient multi-fixture support; added `IBaselineDataProvider`, `FileBasedBaselineProvider`, and `DocumentSnapshot` to enable shared baseline with per-fixture mutation tracking |
| 2.1 | 2026-08-24 | Michael Yarichuk | Merged FilterCompiler + TestFixtures specs under the Mongo.Fakes name; shared filter engine between Core and Server; added `$all` and MongoZen prior-art section |
| 2.0 | 2026-08-24 | Michael Yarichuk | BsonValue-first, Expression-based FilterCompiler rewrite |
| 1.0 | 2026-08-23 | Michael Yarichuk | Initial TestFixtures specification |

---

## Sign-Off

**Specification Status:** Ready for Implementation
**Approach:** BsonValue-native, Expression-compiled, single shared filter engine, ported
operator translators from MongoZen prior art, fuzz-tested against real MongoDB
**Next Step:** Port `ExpressionTranslators` from `vendor/MongoZen`, then implement
`FilterCompiler` and `Mongo.Fakes.Server` skeletons per [`Mongo.Fakes` repo scaffold
plan]
