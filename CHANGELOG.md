# Changelog

## [0.8.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.7...Mongo.Fakes-v0.8.0) (2026-08-26)


### Features

* support string field-path expressions in $project stages ([923c92c](https://github.com/myarichuk/Mongo.Fakes/commit/923c92c0c076c753c30e659f332720654d5900c1))

## [0.7.7](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.6...Mongo.Fakes-v0.7.7) (2026-08-26)


### Bug Fixes

* race condition in testing logic ([04903b3](https://github.com/myarichuk/Mongo.Fakes/commit/04903b322be13c2472be4e1a2da35fe12069edc6))

## [0.7.6](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.5...Mongo.Fakes-v0.7.6) (2026-08-26)


### Bug Fixes

* field-path expressions on arrays now return array of values instead of null ([8e0f551](https://github.com/myarichuk/Mongo.Fakes/commit/8e0f55149a73b25fcd27e9fa72d0de9ba22dc84f))

## [0.7.5](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.4...Mongo.Fakes-v0.7.5) (2026-08-26)


### Bug Fixes

* error on $meta without required query context instead of silently returning null ([e1f8ae1](https://github.com/myarichuk/Mongo.Fakes/commit/e1f8ae1a33d304441ad7b327dd565636bf8c4241))

## [0.7.4](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.3...Mongo.Fakes-v0.7.4) (2026-08-26)


### Bug Fixes

* treat $meta projection fields as additive in all projection contexts ([6f237ae](https://github.com/myarichuk/Mongo.Fakes/commit/6f237aeb5e29a137e2127d81f681e0ecc3f436fd))

## [0.7.3](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.2...Mongo.Fakes-v0.7.3) (2026-08-25)


### Bug Fixes

* stop stripping _id in exclusion-mode projections ([d2f47fc](https://github.com/myarichuk/Mongo.Fakes/commit/d2f47fce466c86c2119edbd90ea6298791ab8906))
* treat $meta projection fields as additive, not restrictive ([eac3452](https://github.com/myarichuk/Mongo.Fakes/commit/eac34524c42fed6b0d3b9507f20ada75115d7f25))

## [0.7.2](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.1...Mongo.Fakes-v0.7.2) (2026-08-25)


### Bug Fixes

* discrepancy between real and fake mongo for a command ([88e1bc0](https://github.com/myarichuk/Mongo.Fakes/commit/88e1bc0668fdeb1d082ed0fb83ae7febf9d313d2))

## [0.7.1](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.7.0...Mongo.Fakes-v0.7.1) (2026-08-25)


### Bug Fixes

* improve BsonPath.SetValueByPath to correctly handle nested arrays and documents ([c7335a7](https://github.com/myarichuk/Mongo.Fakes/commit/c7335a7dcebc2cbe66479502d44121b8932d6c68))
* rescope snapshot store per (database, collection) and fix HandleUpdate ([42c2044](https://github.com/myarichuk/Mongo.Fakes/commit/42c20449a1ae6852d1036cea090b322652dc1b4b))

## [0.7.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.6.0...Mongo.Fakes-v0.7.0) (2026-08-25)


### Features

* implement per-document copy-on-write (CoW) for efficient test fixture isolation ([bb422ce](https://github.com/myarichuk/Mongo.Fakes/commit/bb422ce7ee5a43e5705fe8f59496621e222ae957))

## [0.6.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.5.0...Mongo.Fakes-v0.6.0) (2026-08-25)


### Features

* add findAndModify operator updates, upsert, and returnDocument.After ([53bcfd9](https://github.com/myarichuk/Mongo.Fakes/commit/53bcfd91d4edbd516a441bcbb505dd3cb433ed2d))
* enable computed fields in $project stage ([8202f7d](https://github.com/myarichuk/Mongo.Fakes/commit/8202f7d58a383e896497f56a655ad792688927a8))
* implement $arrayElemAt operator in aggregation expressions ([8818b9a](https://github.com/myarichuk/Mongo.Fakes/commit/8818b9a248abeef685fe29e96cf5b363de146600))


### Bug Fixes

* handle array traversal in BsonPath SetValueByPath and RemoveValueByPath ([205eb0c](https://github.com/myarichuk/Mongo.Fakes/commit/205eb0cbc91228f3ae121ef9681ba95b294b5b82))

## [0.5.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.4.0...Mongo.Fakes-v0.5.0) (2026-08-25)


### Features

* add comprehensive GridFS support and E2E tests ([934ffd1](https://github.com/myarichuk/Mongo.Fakes/commit/934ffd1b6a20edb1732cc8f3d137f3c5e0ebc22b))
* implement ([0e21b5f](https://github.com/myarichuk/Mongo.Fakes/commit/0e21b5f6e3592e40fe47d37b40cda078be42427c))

## [0.4.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.3.1...Mongo.Fakes-v0.4.0) (2026-08-25)


### ⚠ BREAKING CHANGES

* **server:** MongoFakeServer's constructor gained two optional parameters inserted before none existed, which is a binary break for callers compiled against the old 2-arg overload (MissingMethodException at runtime, not caught at compile time).

### Features

* add $nor filter operator and missing update operators ([f330af3](https://github.com/myarichuk/Mongo.Fakes/commit/f330af352b36f7822ea448101bb86018072a4931))
* **server:** add optional SCRAM-SHA-256 authentication ([62591f8](https://github.com/myarichuk/Mongo.Fakes/commit/62591f82173fff9af336052def69240208ea0cfb))

## [0.3.1](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.3.0...Mongo.Fakes-v0.3.1) (2026-08-25)


### Bug Fixes

* pack README.md into Mongo.Fakes.Server nuget package ([4610bf5](https://github.com/myarichuk/Mongo.Fakes/commit/4610bf53e443e2d3cecf4f72b2bfc6d5e7a357b4))

## [0.3.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.6...Mongo.Fakes-v0.3.0) (2026-08-25)


### Features

* **Phase 2 partial:** add missing commands (finding 15) ([27e8fa7](https://github.com/myarichuk/Mongo.Fakes/commit/27e8fa74a127ca12bfcb69014db4ab525b023fe9))
* **Phase 2:** complete findings 16-19 ([7f53b46](https://github.com/myarichuk/Mongo.Fakes/commit/7f53b46cf8ab385c8b24d49b885fbab10c78da64))
* **Phase 3:** infra and docs (findings 20-23) ([3a29f21](https://github.com/myarichuk/Mongo.Fakes/commit/3a29f213e2704d3185adbdf93c214ceba0f28398))
* **Phase 4:** add regression test suite ([fabb65b](https://github.com/myarichuk/Mongo.Fakes/commit/fabb65be57c6613caf9ab191b84c6e419d9f3c70))


### Bug Fixes

* &lt;facepalm&gt; fix release CI config ([ec509f7](https://github.com/myarichuk/Mongo.Fakes/commit/ec509f7488e6da213b8b528d17fd1ee84fa21c6a))
* correct extra-files key in release-please config ([2d35fb9](https://github.com/myarichuk/Mongo.Fakes/commit/2d35fb9cc1bd52304553c18872bcea0a9d30ae30))
* even more &lt;facepalm&gt; - revert earlier 'improvement' in CI ([1fc4961](https://github.com/myarichuk/Mongo.Fakes/commit/1fc49617ad133b9e649a69ceae0c6475116fddf2))
* **Phase 1:** stop returning wrong answers (findings 1-14) ([f7f85fb](https://github.com/myarichuk/Mongo.Fakes/commit/f7f85fbf9e155cb695bc68c756debb44a6bb3092))

## [0.2.6](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.5...Mongo.Fakes-v0.2.6) (2026-08-24)


### Bug Fixes

* OIDC flow vs nuget ([f1fc46c](https://github.com/myarichuk/Mongo.Fakes/commit/f1fc46cb464f3fca74067e21dcfb6db7cc94aaf6))

## [0.2.5](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.4...Mongo.Fakes-v0.2.5) (2026-08-24)


### Bug Fixes

* OIDC nuget username ([6c6cd9f](https://github.com/myarichuk/Mongo.Fakes/commit/6c6cd9fb7b6024a04d7a2cbd1a80ff434c2a3f27))

## [0.2.4](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.3...Mongo.Fakes-v0.2.4) (2026-08-24)


### Bug Fixes

* Update NuGet user for package publishing ([ce2148a](https://github.com/myarichuk/Mongo.Fakes/commit/ce2148a0941f9004aea042f985a0cebc83b05a00))

## [0.2.3](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.2...Mongo.Fakes-v0.2.3) (2026-08-24)


### Bug Fixes

* test release automation 2 ([353b55e](https://github.com/myarichuk/Mongo.Fakes/commit/353b55e257095d575a3f3d96407f291d332c361e))

## [0.2.2](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.1...Mongo.Fakes-v0.2.2) (2026-08-24)


### Bug Fixes

* add user parameter to NuGet/login for OIDC trusted publishing ([776480f](https://github.com/myarichuk/Mongo.Fakes/commit/776480fd2041d8175f7f4ab1e8307e73585d2c02))

## [0.2.1](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.2.0...Mongo.Fakes-v0.2.1) (2026-08-24)


### Bug Fixes

* test release automation ([75d64ec](https://github.com/myarichuk/Mongo.Fakes/commit/75d64ec4deecb7aa3a2ba6e389c521d08ab0edd9))

## [0.2.0](https://github.com/myarichuk/Mongo.Fakes/compare/Mongo.Fakes-v0.1.0...Mongo.Fakes-v0.2.0) (2026-08-24)


### Features

* Add mongodump import functionality and decision guide ([4e4df3b](https://github.com/myarichuk/Mongo.Fakes/commit/4e4df3b3ff6ba6302439d6ddb527d3c531daba64))


### Bug Fixes

* bad implementation of accumulation operators ([9f51e98](https://github.com/myarichuk/Mongo.Fakes/commit/9f51e98fd3a3d5d2a6bc1046d7ca9cd0b8131a5a))
* race on disposal of test fixture ([a34135c](https://github.com/myarichuk/Mongo.Fakes/commit/a34135c5fd24873cf7c898aa90bd0ad032eeaf4f))
