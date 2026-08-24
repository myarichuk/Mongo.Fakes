# Contributing to Mongo.Fakes

## Building & testing

```
dotnet build
dotnet test
```

The solution multi-targets `net8.0` and `net10.0` for the `src/` projects. Test projects
target `net8.0`.

`Mongo.Fakes.Server.Tests` includes an integration suite that spins up a real, ephemeral
`mongod` (via [EphemeralMongo](https://github.com/asimmon/ephemeral-mongo), no Docker
required) and cross-checks filter results against `Mongo.Fakes.Server`'s wire-protocol
double — this is the correctness backstop for filter semantics. It downloads a `mongod`
binary on first run.

## Commit messages

This repo uses [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`,
`fix:`, `docs:`, `chore:`, `refactor:`, etc.) on `main`. Release automation
([release-please](https://github.com/googleapis/release-please)) derives version bumps and
the changelog from these commit messages — non-conventional commit messages on `main` will
be dropped from the generated changelog.

## Pull requests

- Keep PRs scoped to one change; prefer conventional-commit-style PR titles since squash
  merges use the PR title as the commit message.
- Add/update tests for behavior changes — see [Testing Strategy in the
  spec](docs/SPEC.md#testing-strategy) for the expected coverage shape (unit tests over
  compiled predicates, integration tests against real MongoDB, fuzz tests for operator
  semantics).
- New filter operators belong in `Mongo.Fakes.Core` only — `Mongo.Fakes.Server` must not
  reimplement filter matching; it consumes compiled predicates from `Mongo.Fakes.Core`.

## Publishing (maintainers)

NuGet publishing uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC) — no API keys stored in repo secrets. Until Trusted
Publishing is configured on nuget.org for the `Mongo.Fakes.Core` / `Mongo.Fakes.Server`
package IDs, the publish job in `.github/workflows/release.yml` stays disabled. To enable:

1. Reserve the package IDs on nuget.org (first `dotnet nuget push` with an API key, or
   via nuget.org's reserved-prefix flow).
2. Register this repository as a Trusted Publishing source for those package IDs on
   nuget.org (Account Settings → Trusted Publishing).
3. Flip the `publish` job in `.github/workflows/release.yml` from disabled to enabled.
