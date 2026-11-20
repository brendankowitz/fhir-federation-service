# AGENTS.md

Conventions for humans and coding agents working in this repository. Read it before you change code.

## What this is

Ignixa Federation is a read-only FHIR R4 search federation gateway. It fans a search out across shard FHIR servers and merges the results. Today only the scaffold exists: three projects, two health endpoints, telemetry names, CI, and a Docker skeleton.

The design spec is the authority for behaviour: [docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md). Code that conflicts with the spec is a bug, or a spec change that the pull request must call out. [REVIEW.md](REVIEW.md) lists the invariants that reviewers check.

## Repository layout

```text
src/
  Ignixa.Federation.Core/     Domain logic
  Ignixa.Federation.Shards/   HTTP access to backend shards
  Ignixa.Federation.Api/      ASP.NET Core host
test/
  Ignixa.Federation.Core.UnitTests/
  Ignixa.Federation.Shards.UnitTests/
  Ignixa.Federation.Tests.Integration/
deploy/    Dockerfile and compose.yaml
docs/      Documentation and the design spec
samples/   Planned sample client
tools/     Dev scripts
```

## Boundaries

* `Core` is the domain. It has no HTTP and no hosting dependencies.
* `Shards` makes HTTP calls to backends. It has no ASP.NET dependency.
* `Api` is the host. Endpoints, authentication, dependency injection, and response shaping live here.
* Dependencies point one way: `Api` references `Shards` and `Core`, and `Shards` references `Core`.
* Parse external data into domain types at the boundary. Inside the boundary, trust the types and do not re-validate.

## Naming

* Every project, namespace, and public type uses the `Ignixa.Federation` prefix. Namespaces mirror folders.
* No identifier of ours starts with `Fhir` or `FHIR`. "FHIR" is fine in prose.
* The configuration section is `Federation`.
* The `ActivitySource` and the `Meter` are both named `Ignixa.Federation`.

## Code style

* `.editorconfig` defines the style. `EnforceCodeStyleInBuild` is on.
* Nullable reference types are enabled.
* Warnings are errors, with `AnalysisLevel` set to `latest-All`.
* Public API in `src/` needs XML documentation. A missing comment (`CS1591`) fails the build.
* Prefer the simplest design that meets the spec. Add an interface for a real boundary or present variation, not for a test double.

## Package management

* Packages use central management in `Directory.Packages.props`. Project files carry no versions.
* NuGet audit is on and vulnerable packages fail the build. Pin a patched version. Never suppress `NU1901` to `NU1904`.

## Testing

* Tests use xunit.v3 on Microsoft.Testing.Platform, with Shouldly for assertions. Use NSubstitute when a test needs a test double, and add it to `Directory.Packages.props`.
* `global.json` selects the Microsoft.Testing.Platform runner. `dotnet test --solution` needs it.
* Test projects are executables. `Directory.Build.props` sets `OutputType` to `Exe` for everything under `test/`. They need no `Microsoft.NET.Test.Sdk`.
* Every test project needs at least one test. A test project with zero tests exits with code 8, "zero tests ran".
* Test behaviour that a caller can observe, not implementation. A test should fail when the behaviour breaks and survive an internal refactor.
* The suite has 13 tests today: Core 4, Shards 7, Integration 2.

## Build, test, and run

Run every command from the repository root with the SDK pinned by `global.json` (11.0.100). If the `dotnet` on your PATH resolves a different SDK, run the `dotnet` that has 11.0.100 installed.

```sh
dotnet restore Ignixa.Federation.slnx
dotnet build Ignixa.Federation.slnx -c Release
dotnet test --solution Ignixa.Federation.slnx -c Release
```

Run one test project:

```sh
dotnet test --project test/Ignixa.Federation.Core.UnitTests/Ignixa.Federation.Core.UnitTests.csproj -c Release
```

Run one test class, or tests that match a method name. Use `--filter-class` and `--filter-method` with `--project`.

```sh
dotnet test --project test/Ignixa.Federation.Shards.UnitTests/Ignixa.Federation.Shards.UnitTests.csproj -c Release --filter-class Ignixa.Federation.Shards.UnitTests.Diagnostics.ShardActivityTests
dotnet test --project <csproj> -c Release --filter-method "*TagNames_AreTheDocumentedValues"
```

Do not combine a filter with `--solution`. Every project where nothing matches exits non-zero with code 8.

Run the host and check health:

```sh
dotnet run --project src/Ignixa.Federation.Api --urls http://localhost:5201
curl http://localhost:5201/health/live
curl http://localhost:5201/health/ready
```

Both health endpoints return 200. The host maps only `/health/live`, which runs no checks, and `/health/ready`, which runs checks tagged `ready`. No checks are registered yet. The host does not read the `Federation` section yet.

## Telemetry

* `Ignixa.Federation.Core.Diagnostics.FederationTelemetry` owns the `ActivitySource` and the `Meter`.
* `Ignixa.Federation.Shards.Diagnostics.ShardActivity.StartBackendCall(alias, stage)` starts the span for one backend call.

## Docker

`deploy/Dockerfile` builds the host. `deploy/compose.yaml` runs it with placeholder busybox shards. See [deploy/README.md](deploy/README.md).

## Commits and pull requests

* Commit subjects use `type(scope): subject`, for example `feat(shards): add the shard registry model`.
* Pull request bodies follow `.github/pull_request_template.md`.
* Design changes go through the spec first. See [CONTRIBUTING.md](CONTRIBUTING.md).
