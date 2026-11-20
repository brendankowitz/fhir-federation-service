# Tests

Test projects use xunit.v3 on Microsoft.Testing.Platform, with Shouldly.

| Project | Covers |
|---|---|
| `Ignixa.Federation.Core.UnitTests` | The `Core` telemetry names (`FederationTelemetry`) and the absence of HTTP and hosting references. Identity types, the shard contract types, shard registry validation, the registry version hash against golden values, `FederationOptions` validation, binding the spec §4.2 example, and the absence of public setters on contract types. |
| `Ignixa.Federation.Shards.UnitTests` | `ShardActivity` spans for backend calls. |
| `Ignixa.Federation.Tests.Integration` | The hosted `Api`: `/health/live` and `/health/ready`, including which checks each endpoint runs. The observed-page comparator for the differential oracle harness (`Oracle/`), which checks hand-authored searchset pages against the [federation-oracle fixtures](fixtures/federation-oracle/README.md). It does not run a federation or a live backend. |

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
