# Tests

Test projects use xunit.v3 on Microsoft.Testing.Platform, with Shouldly. There are 13 tests today.

| Project | Covers | Tests |
|---|---|---|
| `Ignixa.Federation.Core.UnitTests` | The `Core` telemetry names (`FederationTelemetry`). | 4 |
| `Ignixa.Federation.Shards.UnitTests` | `ShardActivity` spans for backend calls. | 7 |
| `Ignixa.Federation.Tests.Integration` | The hosted `Api`: `/health/live` and `/health/ready`. | 2 |

`Ignixa.Federation.Tests.Compatibility` is deferred. The spec plans it for certified backend profile runs ([§12.1](../docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#121-projects), [§15](../docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#15-testing)), and it will arrive in a later PR.

## Commands

Run from the repository root with the SDK pinned by `global.json`.

```sh
dotnet test --solution Ignixa.Federation.slnx -c Release
dotnet test --project test/Ignixa.Federation.Core.UnitTests/Ignixa.Federation.Core.UnitTests.csproj -c Release
dotnet test --project test/Ignixa.Federation.Shards.UnitTests/Ignixa.Federation.Shards.UnitTests.csproj -c Release --filter-class Ignixa.Federation.Shards.UnitTests.Diagnostics.ShardActivityTests
dotnet test --project <csproj> -c Release --filter-method "*TagNames_AreTheDocumentedValues"
```

Use `--filter-class` and `--filter-method` with `--project`, not with `--solution`. See [AGENTS.md](../AGENTS.md) for why, and for the rule that every test project needs at least one test.
