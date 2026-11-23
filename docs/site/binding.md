---
title: Bind the API to a backend
description: Identify the services a host must supply, when configuration is validated, and how a generation becomes eligible to serve.
sidebar_position: 6
---

# Bind the API to a backend

**Binding is host composition, not a connection string.** `Federation:Host` configures the public surface; `Federation:Shards` describes the owners. Neither creates an `IShardClientFactory` nor proves that any shard supports a query. The ordinary executable registers the API; the [integration host][fixture] shows how tests supply the missing pieces. Its scripted transport is not a deployable backend. [Startup registration][routes] · [Search construction][dispatch].

## Choose one publication path

| Host composition | Result |
| --- | --- |
| No `Federation:Host`, no explicit backend mode | The probes run, but search and metadata return 503. [Endpoint registration][routes] · [Metadata][metadata] |
| Host settings; no explicit backend mode | With no registered `SearchCapabilities`, the host listens cold. A trusted component can publish a view later through `IFederationCapabilityPublisher`. A supplied startup view is prepared eagerly; invalid settings or a missing transport fail startup. Withdrawing the view does not restore the seed. [Generation state][state] · [Publication tests][publication] |
| Host settings; explicit `Federation:Backend:Mode=PrototypeCatalog` | One worker owns publication. It ignores a registered seed and does not register the manual publisher. The mode requires an `ICapabilityAdmissionSource`; the stock host fails before listening without it. [Mode selection and registration][routes] · [Catalog startup tests][catalog] |

In catalog mode the source may decline admission, leaving the host cold. When it supplies evidence, the worker prepares search and metadata together, then asks the source to install the result **only if the evidence is still current**. A request cannot capture a stale generation merely because the worker has not yet withdrawn it. This is a contract with the source, not a substitute for verifying real servers. [Admission contract][source] · [Worker][worker] · [Generation state][state].

## Check what the host actually registered

| To serve… | Required binding or evidence |
| --- | --- |
| Any search page | A capability view whose registry matches the configured one, valid options, and an `IShardClientFactory`. Its client meters every physical send and consumed response chunk against the request budget; its fit check has no side effects. [Search construction][dispatch] · [Factory contract][factory] |
| Exact count or distributed `_has` | A complete valid shard-to-`CountDialect` map. Paged distributed `_has` uses the map to measure generated requests; it need not execute a count. Count-only queries additionally need exact-count proof for the shape. [Dispatch][dispatch] · [Reverse-count tests][counts] |
| Distributed forward chain or paged `_revinclude` | `IDistributedForwardSearch` or `IRevIncludePageResolver`, respectively, plus admitted paths and grants. Their absence does not disable ordinary searches. [Forward tests][forward] · [Reverse-include tests][includes] |
| Owner-qualified reads | `ProxyReads`, an `IShardReadBinding` and an eligible searchable type. A proof-backed admission can restrict readable types further. Check the metadata `read` interaction, not just the setting. [Read eligibility tests][reads] · [Metadata generation][metadata] |

## Diagnose validation in the right order

1. If the host **does not listen**, inspect the named `Federation:Host` key or explicit mode first. For a supplied startup view, also check missing transport and registry/profile mismatch. [Registration][routes] · [Search construction][dispatch].
2. If it **listens but stays cold**, do not treat that as successful shard validation. The state captures raw startup options; binding and validation wait for a view. Correct the deployment settings and rebuild the generation with the intended registry. [Generation state][state].
3. If **metadata works but search does not**, check signer readiness, type grants, then the [search support matrix](status.md). Adding a serializer, count map or resolver cannot make an arbitrary combination executable. [Signing tests][signing] · [Dispatch][dispatch].

[fixture]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/SearchHost.cs
[routes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[dispatch]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[state]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchState.cs
[source]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/ICapabilityAdmissionSource.cs
[worker]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/CapabilityPublicationWorker.cs
[catalog]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/CatalogBackendHostTests.cs
[publication]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchCapabilityPublicationTests.cs
[metadata]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[factory]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/IShardClientFactory.cs
[counts]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchReverseCountTests.cs
[forward]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchDistributedForwardTests.cs
[includes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[reads]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadEligibilityTests.cs
[signing]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
