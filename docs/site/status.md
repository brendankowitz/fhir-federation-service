---
title: Search support
description: Which FHIR R4 searches the HTTP gateway executes, which require host bindings, and which exist only in Core or the design.
sidebar_position: 4
---

# Search support

**A valid FHIR search is not necessarily an executable gateway search.** The [planner] validates the request against a particular capability view. The [HTTP dispatcher] then chooses an execution path. The matrix below describes that second step on a **configured, ready host** with the required permissions. It does not mean the stock container can connect to a FHIR server.

[PR #17](https://github.com/brendankowitz/fhir-federation-service/pull/17) supplied the API in this checkout. Integration tests compose it with a capability view and scripted shards. The stock executable does not register the production shard transport or admission source. The verified HTTPS backend proposed in [PR #19](https://github.com/brendankowitz/fhir-federation-service/pull/19) is not part of this baseline. See [Operate a gateway](operator.md) for the difference between an unconfigured, cold, and ready host, and [Configuration](https://brendankowitz.github.io/fhir-federation-service/configuration/) for option defaults.

## What can run through the API?

Examples assume the named types, parameters and reference paths are admitted. “Conditional” means an additional service or capability proof is required, not that a client can turn the feature on with a query flag.

| Request | HTTP status of the feature | Evidence and limits |
| --- | --- | --- |
| `GET /Patient?name=Smith` or form `POST /Patient/_search` | Implemented: ordinary unsorted search | Returns a JSON `searchset` in registry order. `_count` sets match page size; absent `_total` or `_total=none` omits `Bundle.total`. POST combines URL occurrences before body occurrences. Only supported predicates, repetitions, alternatives and modifiers pass the planner; it does not silently drop an unknown filter. [Endpoint tests][primary], [predicate tests][predicates]. |
| `GET /Patient?ct=…` | Implemented: inline continuation | Follow the returned URL unchanged with the same caller; `ct` must be the only parameter and the method must be GET. The token carries replay state, not a public shard cursor. A successor too large for inline or ingress limits fails the page with 503; the HTTP path does not spill to Redis. [Continuation tests][continuations], [HTTP dispatcher]. |
| `_count=0` or `_summary=count` with ordinary predicates | Conditional: exact count only | Requires a complete per-shard count dialect map and exact-count evidence for this query. Sums physical owner counts; no entries or next page. If a required shard fails there is no total, not an estimate. [Count tests][count]. |
| `Observation?subject:Patient.name=Smith`, passthrough | Conditional: depth-one forward chain | With operator-asserted local placement and native proof, the dispatcher keeps the chain on the shard request. Requires the target type grant, including on replay. Unsorted pages only; this does not enable chain counts. [Passthrough tests][local-chain]. |
| Same forward chain, distributed | Conditional: **one** distributed forward hop | `IDistributedForwardSearch` discovers witnesses and supplies owner-specific requests; replay checks the saved requests without rediscovering witnesses. Missing strategy: 501 on a new otherwise valid search. Can keep ordinary predicates, but not combine this hop with another chain, include, sort, count or paged total. [Distributed endpoint tests][forward], [Core resolver][forward-core]. |
| `Patient?_has:Observation:subject:status=final` | Conditional: depth-one **distributed** reverse chain | Pages owner-specific matches after witness discovery. Multiple `_has` clauses intersect by owner and ID, not ID alone. Requires the witness type grant and an explicit dialect map even for pages that never count. A valid **passthrough** `_has` plan is not executed. [Reverse-chain tests][reverse]. |
| Same distributed `_has` with `_summary=count` or `_count=0` | Conditional: exact reverse-chain count | Resolves witnesses first, then counts on surviving owners within the remaining budget. No witness keys → exact zero; a dangling witness is not itself a match. [Reverse-count tests][reverse-count]. |
| `Patient?_revinclude=Observation:subject` | Conditional: reverse includes for an ordinary unsorted page | `IRevIncludePageResolver` gets exactly the emitted matches; output keeps owning shard identities. A denied include type is omitted with a generic outcome, not a forbidden primary search. Overflow adds an incomplete outcome. No resolver → valid shape not executed; no count, paged total, sort, chain or forward include combination. [Endpoint tests][revinclude], [Core executor][revinclude-core]. |

## Not available over HTTP

| Request | What exists | What to expect |
| --- | --- | --- |
| `_sort=_lastUpdated` or `_sort=-_lastUpdated` | Certified planner shape and [Core sorted-page merge tests][sort]. | Even when certified, the dispatcher does not execute a sorted HTTP page. Other sort keys and multi-key sort are outside v1. |
| `_total=accurate` or `_total=estimate` **on a page** | The [v1 design] calls for exact counts when certified, not a guessed total. | A paged plan with `IncludeTotal` is refused even when a count-only request works. Use a separate count-only query if needed; it is a separate observation, not a snapshot guarantee. [Paged-total refusal][count]. |
| `Observation?_include=Observation:subject` | Planner admission and [forward-include fetch tests][include]. | No forward-include execution branch in the [HTTP dispatcher]. An explicit `_revinclude` is a different feature. |
| Deeper chains, wildcard or `:iterate` includes, `_elements`, non-count `_summary`, non-JSON representation | Not supported in v1; see [control tests][controls], [include planner tests][include-plan] and the [v1 design]. | Unsupported search parameters are not forwarded as best effort. Unsupported representation or POST media type gets 415, not an XML Bundle. |
| System-wide or cross-type search, writes, history, vread and `$` operations | Not part of the implemented type-search routes. Bulk jobs and write placement are [future work][vnext]. | Do not rely on the spec's proposed FHIR refusal body for an unmapped path; normal router 404/405 can apply. |

For an authenticated search on a warm host, **400 `not-supported`** can mean the planner lacks proof for that parameter or shape. **501 `not-supported`** means the plan is valid but the host has no execution path for it. A **503** before planning can instead mean the generation or signing keys are unavailable. Narrowing the query will not fix a cold host.

## Discovery, reads and identity

`GET /metadata` serves a public R4 CapabilityStatement from the current generation. It lists searchable types and parameters, eligible proxy reads, and reverse includes only when there is a resolver. It is not a promise that every combination executes. [Metadata implementation][metadata].

Two shards' `Patient/42` entries are distinct: `entry.fullUrl` points to the actual owner, and the resource JSON and its relative references stay unchanged. Where `ProxyReads`, the read binding, and type eligibility allow it, an alternate link uses `/shards/{alias}/{type}/{id}`. The unqualified `/{type}/{id}` is ambiguous, not a lookup across all shards. [Response tests][responses], [read route tests][reads].

[planner]: ../../src/Ignixa.Federation.Core/Search/SearchPlanner.cs
[HTTP dispatcher]: ../../src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[primary]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchTests.cs
[predicates]: ../../test/Ignixa.Federation.Core.UnitTests/Search/SearchPlannerPredicateTests.cs
[continuations]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[count]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchCountTests.cs
[local-chain]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchForwardChainTests.cs
[forward]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchDistributedForwardTests.cs
[forward-core]: ../../src/Ignixa.Federation.Core/Execution/Chains/ForwardChainResolver.cs
[reverse]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchReverseChainTests.cs
[reverse-count]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchReverseCountTests.cs
[revinclude]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[revinclude-core]: ../../src/Ignixa.Federation.Core/Execution/References/RevIncludePageExecutor.cs
[sort]: ../../test/Ignixa.Federation.Core.UnitTests/Execution/Sorted/SortedPageExecutorMergeTests.cs
[include]: ../../test/Ignixa.Federation.Core.UnitTests/Execution/Includes/ForwardIncludeFetchTests.cs
[controls]: ../../test/Ignixa.Federation.Core.UnitTests/Search/SearchPlannerControlTests.cs
[include-plan]: ../../test/Ignixa.Federation.Core.UnitTests/Search/SearchPlannerIncludeTests.cs
[v1 design]: ../superpowers/specs/2026-11-19-fhir-federation-service-design.md
[vnext]: ../vnext/README.md
[metadata]: ../../src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[responses]: ../../test/Ignixa.Federation.Tests.Integration/Responses/SearchsetResponseTests.cs
[reads]: ../../test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadRouteTests.cs
