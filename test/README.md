# Tests

Test projects use xunit.v3 on Microsoft.Testing.Platform, with Shouldly.

| Project | Covers |
|---|---|
| `Ignixa.Federation.Core.UnitTests` | The `Core` telemetry names (`FederationTelemetry`) and the absence of HTTP and hosting references. Identity types, the shard contract types, shard registry validation, the registry version hash against golden values, `FederationOptions` validation, binding the spec §4.2 example, and the absence of public setters on contract types. The unsorted page executor (`Execution/`) against scripted in-memory shard clients: the first live shard queried alone, then bounded speculative fanout that counts buffered pages; emission in registry ordinal then backend order, including short and empty pages, out-of-order completion, and entries that share a type and id on different shards; needed versus speculative failures and cancellation; the page-fetch budget; cursor cycles, including across continuations; and resuming from a continuation, including replay of a partially emitted page and a missing replay anchor. It does not use HTTP or a live backend. |
| `Ignixa.Federation.Shards.UnitTests` | `ShardActivity` spans for backend calls. |
| `Ignixa.Federation.Tests.Integration` | The hosted `Api`: `/health/live` and `/health/ready`, including which checks each endpoint runs. The observed-page comparator for the differential oracle harness (`Oracle/`), which checks hand-authored searchset pages against the [federation-oracle fixtures](fixtures/federation-oracle/README.md). It does not run a federation or a live backend. |
| `Ignixa.Federation.SampleClient.UnitTests` | The [sample client](../samples/Ignixa.Federation.SampleClient/README.md) against scripted HTTPS responses from an in-memory `HttpMessageHandler`: opaque `next` paging, match, include and `OperationOutcome` entries, keeping entries that share a type and id apart by `fullUrl`, the source-shard alias, `410 Gone` surfaced as a restart, errors that hide bodies and tokens, credential confinement for `next`, alternate and direct-shard reads, transports that do not follow redirects, and the CLI. It does not run a federation, shards, or a live backend. |

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
