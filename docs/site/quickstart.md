---
title: Run a local gateway
description: Smoke-test the stock image and distinguish an unconfigured host from a configured but cold one.
sidebar_position: 4
---

# Run a local gateway

This exercise needs no patient data, credentials or FHIR servers. The two servers in the [Compose file][compose] run BusyBox `httpd`; they test container wiring, not backend compatibility.

## 1. Start one host

From the repository root, run the [Compose stack][compose]:

```sh
docker compose -f deploy/compose.yaml up -d --build
docker compose -f deploy/compose.yaml ps
```

Alternatively, with the [pinned SDK][sdk], start just the API in a terminal and leave it running: `dotnet run --project src/Ignixa.Federation.Api --urls http://localhost:5201`. Use **one** method so both processes do not compete for port 5201. [Repository run instructions][run].

## 2. Check process, readiness, then service

```sh
curl -i http://127.0.0.1:5201/health/live
curl -i http://127.0.0.1:5201/health/ready
curl -i http://127.0.0.1:5201/metadata
```

| Stock result | What you proved |
| --- | --- |
| `/health/live` → 200 | The process answers. The liveness predicate selects no dependency checks. [Host startup][startup] |
| `/health/ready` → 200 | No check tagged `ready` is failing. Without `Federation:Host` there are no built-in dependency checks to fail. [Probe tests][probes] · [Route registration][routes] |
| `/metadata` → 503 | No federation view is available. This is the expected result, **not** a sign that the BusyBox shards need more startup time. [Metadata endpoint][metadata] |

If the first request cannot connect, inspect `docker compose -f deploy/compose.yaml logs federation` and confirm that you are probing **host loopback 5201**, not container port 8080. When finished, `docker compose -f deploy/compose.yaml down`, or Ctrl+C for the SDK host. [Port mapping][compose].

## 3. Verify the next state without connecting a shard

Run the [configured-container example](deployment.md). With a complete `Federation:Host` section but no current view, expect **live 200, ready 503, metadata 503**. A partial section can fail startup instead. Do not enable `PrototypeCatalog` to fix readiness: without the admission service the stock executable cannot start in that mode. [Cold-host tests][cold] · [Catalog startup tests][catalog].

To exercise a **successful search** in this checkout without mistaking a test double for a production backend, run the [primary search integration tests][search] with the [repository test runner][run]:

```sh
dotnet test --project test/Ignixa.Federation.Tests.Integration/Ignixa.Federation.Tests.Integration.csproj -c Release --filter-class '*PrimarySearchTests'
```

[compose]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/deploy/compose.yaml
[sdk]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/global.json
[run]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/AGENTS.md
[startup]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Program.cs
[routes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[probes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/HealthEndpointTests.cs
[metadata]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[cold]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs
[catalog]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/CatalogBackendHostTests.cs
[search]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchTests.cs
