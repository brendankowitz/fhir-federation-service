<div align="center">
  <img src="assets/ignixa-federation-logo.svg" alt="Ignixa Federation logo" width="120"/>
  <h1>Ignixa Federation</h1>
  <p>
    <b>One read-only FHIR R4 search endpoint in front of many FHIR servers</b>
  </p>

[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![CI](https://github.com/brendankowitz/fhir-federation-service/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/brendankowitz/fhir-federation-service/actions/workflows/ci.yml)
[![FHIR R4](https://img.shields.io/badge/FHIR-R4-orange)](https://hl7.org/fhir/R4/)
[![Status: early implementation](https://img.shields.io/badge/status-early%20implementation-orange)](#project-status-by-capability)

</div>

---

> **Project status.** Core search planning and execution components are implemented and covered by scripted unit tests. The authenticated search API has merged ([#17](https://github.com/brendankowitz/fhir-federation-service/pull/17)); the verified HTTPS shard backend remains in review ([#19](https://github.com/brendankowitz/fhir-federation-service/pull/19)). The earlier scaffold served health checks only. The merged API needs host configuration, a capability view, and a request-metered shard client to serve searches; the stock host does not supply a production shard transport. This is early implementation work, not a production-ready release. The [design spec](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md) defines intended behavior; the [status table](#project-status-by-capability) distinguishes implemented, in-review, and planned work.

---

## Overview

Ignixa Federation is being built to present N FHIR R4 servers, called shards, as one read-only search endpoint. A configured host can fan out supported searches, merge the answers, and return a `searchset` Bundle with paging links. The merged API uses the Core planner and unsorted executor; not every Core execution stage is wired into HTTP.

Large datasets outgrow a single FHIR server and its single write primary. Splitting the data across servers fixes the storage limit but leaves every client to query each server and combine the results. The federation does that work once, in one place, with defined behaviour for paging, ordering, and failure.

The v1 service is designed to be read-only: it will never write to a shard, and a request it cannot answer completely will fail rather than return a partial Bundle. End-to-end behavior with live shards still depends on the backend integration.

![Conceptual federated search: a client queries a gateway that fans out to independent FHIR servers and merges their results](assets/architecture/fhir-federated-querying-at-scale.svg)

This diagram illustrates the target search design. An entry's `fullUrl` identifies its owning shard; a direct read requires authorization at that shard. `Bundle.link[relation=next]` points to the gateway. The capacity arithmetic is illustrative, not a measured storage, latency, or throughput claim.

## ✨ Key Features

The components below describe what is implemented in Core and what remains in review or planned. Core tests use scripted shard clients; they do not prove that the current API runs these behaviors end to end. See the [status table](#project-status-by-capability).

### Implemented in Core

* Search planning, parameter rewriting, and reference classification.
* Unsorted and `_lastUpdated` sorted page executors, exact federated count execution, and page-scoped include / reverse-include execution.
* Distributed forward-chain and depth-one reverse-chain (`_has`) resolution.

The API uses some Core capabilities, but does not directly call `ForwardChainResolver`, `RevIncludePageExecutor`, or `SortedPageExecutor`. Distributed forward chains and reverse includes require optional host-provided strategies; sorted HTTP pages are not implemented.

### Implemented in the API

* Authenticated read-only FHIR R4 search routes, `/metadata`, and inline HTTP continuations ([#17](https://github.com/brendankowitz/fhir-federation-service/pull/17)). Redis continuation storage is a separate component; HTTP paging does not use it.

### In review

* Verified HTTPS shard access and shard configuration ([#19](https://github.com/brendankowitz/fhir-federation-service/pull/19)).

FHIR R4 is the only certified release today. The VP1 follow-up will make search version-provider based, selected by `Federation:Version`.

## Project status by capability

| Capability | Status | Spec |
|---|---|---|
| 🏗️ .NET solution, shard registry contract, identity types, and options validation | Done ([#4](https://github.com/brendankowitz/fhir-federation-service/pull/4)) | [§4.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#42-shard-registry), [§12.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#121-projects) |
| 📊 CI, health endpoints, and telemetry foundations | Done ([#4](https://github.com/brendankowitz/fhir-federation-service/pull/4)) | [§14](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#14-observability), [§16](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#16-phases) |
| 🔎 Query planner, classifier, rewriter, and reference classifier | Done ([#10](https://github.com/brendankowitz/fhir-federation-service/pull/10), [#12](https://github.com/brendankowitz/fhir-federation-service/pull/12)) | [§6](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#6-search-planning), [§7.7](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#77-reference-classifier-shared) |
| 🔎 Unsorted primary page executor | Done ([#9](https://github.com/brendankowitz/fhir-federation-service/pull/9)) | [§7.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#71-primary-search-and-emission-order) |
| 🔎 Forward include, reverse include, distributed forward chains, and `_has` | Done ([#13](https://github.com/brendankowitz/fhir-federation-service/pull/13), [#15](https://github.com/brendankowitz/fhir-federation-service/pull/15), [#18](https://github.com/brendankowitz/fhir-federation-service/pull/18)) | [§7.5](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#75-chains-and-_has-depth-1), [§7.6](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#76-includes-and-revincludes-targeted-per-page) |
| 🧭 Sorted `_lastUpdated` page executor | Done ([#16](https://github.com/brendankowitz/fhir-federation-service/pull/16)) | [§7.3](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#73-merge-sorted) |
| 🧭 Exact federated count execution | Done ([#14](https://github.com/brendankowitz/fhir-federation-service/pull/14)) | [§7.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#71-primary-search-and-emission-order), [§7.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#72-count-only) |
| 🛡️ Authenticated FHIR R4 search API, `/metadata`, and inline HTTP continuations; separate Redis continuation component | Done ([#17](https://github.com/brendankowitz/fhir-federation-service/pull/17)); Redis is not wired to HTTP paging | [§5](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#5-request-surface), [§7.4](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#74-continuation-tokens), [§9](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#9-capabilities), [§10](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#10-security) |
| 🛡️ Verified HTTPS shard backend and configuration | In review ([#19](https://github.com/brendankowitz/fhir-federation-service/pull/19)) | [§6.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#62-backend-strictness-fail-closed-on-dropped-filters), [§9.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#92-backend-profiles), [§10.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#102-backend-credentials) |
| 💻 Sample client | Done ([#8](https://github.com/brendankowitz/fhir-federation-service/pull/8)); scripted tests only | [samples/README.md](samples/README.md) |
| 🧪 Live backend compatibility and differential federation tests | Planned | [§15](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#15-testing) |
| 🧪 Load tests at 16 and 50 shards | Planned | [§3](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#3-constraints), [§15](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#15-testing) |
| 🔎 Search version-provider selection via `Federation:Version` (VP1) | Planned | [§17](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#17-left-to-the-builder) |
| 🚧 Write placement, import/export, and shard rebalancing | Proposed for vNext; not implemented | [Write placement](docs/vnext/write-placement.md), [Import and export](docs/vnext/import-export.md), [vNext overview](docs/vnext/README.md) |

## Architecture

`Core` contains the domain contracts, planner, and execution components, with no HTTP code. The merged `Api` maps search and metadata routes, but needs a capability view and shard client factory supplied by the host. The verified HTTP client remains in review. The diagram below depicts the **earlier health-only scaffold**: its references to `main` and an unwired search path describe that older snapshot, not the merged API.

```mermaid
flowchart LR
  Client[Client] --> Api[Api host: health endpoints only]
  Core[Core planner and execution components]
  Api -. search not wired on main .-> Core
  Core --> Contract[IShardClient contract]
  Contract -. HTTPS implementation in review .-> Shards[Shard client: #19]
  Shards -.-> ShardA[(Shard A FHIR server)]
  Shards -.-> ShardB[(Shard B FHIR server)]
  Client -. follows fullUrl .-> ShardA
```

The `Core` components are exercised with scripted clients, not live shard servers. See [docs/architecture](docs/architecture/README.md) for the design diagram and the spec for the complete target architecture.

## How a search runs

This is the target flow for a sorted search across two shards, not a request the current host can serve. The sorted executor exists in `Core`, but the API does not call it; `ForwardChainResolver` and `RevIncludePageExecutor` are likewise not called directly. The merged API can execute supported unsorted searches when its required host bindings are supplied. See [§7.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#71-primary-search-and-emission-order), [§7.3](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#73-merge-sorted), and [§7.4](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#74-continuation-tokens) for intended behavior.

```mermaid
sequenceDiagram
  participant C as Client
  participant F as Federation
  participant A as Shard A
  participant B as Shard B
  C->>F: GET /Observation?code=X&_sort=_lastUpdated
  par fan-out
    F->>A: search with strict handling
    A-->>F: page of matches
  and
    F->>B: search with strict handling
    B-->>F: page of matches
  end
  F->>F: merge the per-shard pages in sort order
  F-->>C: searchset Bundle with a next link carrying an opaque cursor
  C->>F: GET /Observation?ct=cursor
```

## Proposed vNext

![Proposed data coordination: a query broker and bulk workers share a placement catalog and coordinate independent FHIR servers and object storage](assets/architecture/fhir-federation-coordination.svg)

This is a proposed architecture, not current write, import, or export support. The [vNext roadmap](docs/vnext/README.md) describes the placement, bulk-job, and rebalancing proposals. The capacity arithmetic is illustrative; it does not establish usable capacity or performance.

## Quickstart

Prerequisite: the .NET SDK version in [global.json](global.json), currently 11.0.100. If the `dotnet` on your PATH resolves a different SDK, run the `dotnet` that has 11.0.100 installed.

```sh
git clone https://github.com/brendankowitz/fhir-federation-service.git
cd fhir-federation-service
dotnet restore Ignixa.Federation.slnx
dotnet build Ignixa.Federation.slnx -c Release
dotnet test --solution Ignixa.Federation.slnx -c Release
```

Run the host and check the health endpoints. Both return 200. The host runs in the foreground, so use two terminals.

Terminal 1, run the host:

```sh
dotnet run --project src/Ignixa.Federation.Api --urls http://localhost:5201
```

Terminal 2, check health:

```sh
curl http://localhost:5201/health/live
curl http://localhost:5201/health/ready
```

The sample client builds with the solution. Print its usage with `dotnet run --project samples/Ignixa.Federation.SampleClient -c Release -- --help`; its tests run with `dotnet test --project test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj -c Release`. A live federation requires the host bindings and a shard transport; the stock host does not provide a production transport. See [samples/README.md](samples/README.md).

Docker instructions are in [deploy/README.md](deploy/README.md).

## Configuration

The `Federation` section describes the shard registry and query policy through `FederationOptions` in Core. The merged search API uses these options when preparing a search service; the earlier health-only scaffold did not bind them. The following keys are represented by the Core options types:

```text
Federation:
  Shards[]: Id, Alias, BaseUrl, Profile, Auth, LinkBaseAliases[]
    Auth: Type, Resource, ClientId, TenantId, SecretName
  ProxyReads
  ChainResolution: Default, Passthrough[]
  Limits: DefaultPageSize, MaxPageSize, MaxChainKeys, MaxChainSourceRows,
          MaxIncludesPerPage, BackendCallsPerShard, MaxResponseBytes,
          MaxParallelBackendCalls, ShardCountWarningThreshold
  Timeouts: Request, BackendCall, ChainSubquery
  Tokens: MaxInlineTokenBytes, SlidingLifetime, AbsoluteLifetime
```

`Auth.Type` supports `ManagedIdentity` and `ClientCredentials`; `SecretName` points to an externally held secret, never a secret value. Defaults and validation are defined in the Core options types. `Federation:Version` is not currently an option; it is planned for VP1.

For example, the following two-owner registry and policy section is accepted by the Core options validator when the `MicrosoftFhirServer` profile is supplied:

```json
{
  "Federation": {
    "ProxyReads": true,
    "ChainResolution": {
      "Default": "Distributed",
      "Passthrough": [ "Observation.subject:Patient", "Encounter.subject:Patient", "Condition.subject:Patient" ]
    },
    "Shards": [
      { "Id": "east", "Alias": "k7q2", "BaseUrl": "https://fhir-east.internal/", "Profile": "MicrosoftFhirServer",
        "Auth": { "Type": "ManagedIdentity", "Resource": "https://fhir-east.internal" },
        "LinkBaseAliases": [] },
      { "Id": "west", "Alias": "p9xm", "BaseUrl": "https://fhir-west.internal/", "Profile": "MicrosoftFhirServer",
        "Auth": { "Type": "ManagedIdentity", "Resource": "https://fhir-west.internal" } }
    ]
  }
}
```

The hostnames and token audiences are placeholders, not runnable endpoints. The passthrough paths are illustrative operator assertions that those references are shard-local; use them only when that is true. This section alone is not enough to serve searches: the merged API also needs `Federation:Host` settings, a capability view, and a request-metered shard client factory. The earlier health-only scaffold does not expose a search endpoint.

## Repository layout

```text
.github/       CI workflow, issue templates, pull request template
assets/        Logo
deploy/        Dockerfile and compose.yaml
docs/          Documentation, including the design spec
samples/
  Ignixa.Federation.SampleClient/   Standalone wire-contract client
src/
  Ignixa.Federation.Core/     Domain logic, no HTTP or hosting
  Ignixa.Federation.Shards/   Backend transport (HTTPS implementation in review)
  Ignixa.Federation.Api/      ASP.NET Core host (search and health endpoints)
test/
  Ignixa.Federation.Core.UnitTests/
  Ignixa.Federation.Shards.UnitTests/
  Ignixa.Federation.Tests.Integration/
  Ignixa.Federation.SampleClient.UnitTests/
tools/         Developer scripts
```

## Documentation

* [Documentation index](docs/README.md)
* [Architecture](docs/architecture/README.md)
* [Guides](docs/guides/README.md)
* [vNext](docs/vnext/README.md)
* [Design spec](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md)

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) for the branch and pull request flow. [AGENTS.md](AGENTS.md) holds the conventions, boundaries, and commands for humans and coding agents.

## Security

Report vulnerabilities privately. See [SECURITY.md](SECURITY.md).

## License

Ignixa Federation is released under the [MIT License](LICENSE).
