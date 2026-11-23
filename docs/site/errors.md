---
title: Diagnose HTTP errors and OperationOutcome
sidebar_position: 12
description: Identify the failing gateway boundary from the route, HTTP status and FHIR issue code, then choose a safe recovery action.
---

# Diagnose HTTP errors and OperationOutcome

Start with **the route, HTTP status, and `issue[].code` together**. A `410` following a Bundle's `next` link means restart the search; a `410` reading a resource means the owner reports it deleted. A `401` from the gateway is about the caller's token, not the gateway's shard credential. [Search and replay][search] · [owner reads][reads] · [failure design, §8][spec]

## Recognize the response

A handled gateway refusal replaces the page or resource with `Content-Type: application/fhir+json; charset=utf-8` and one error issue. For example, this is the body for an expired continuation. Branch on status and code, not the prose. Diagnostics are chosen by the gateway; they can identify a configured alias or a zero-based parameter position, not the raw query or backend exception. [Outcome writer][wire] · [planner diagnostics][planner] · [disclosure tests][failures]

```json
{
  "resourceType": "OperationOutcome",
  "issue": [{
    "severity": "error",
    "code": "not-found",
    "diagnostics": "The continuation is no longer valid; repeat the original search."
  }]
}
```

**A warning inside a successful searchset is different.** For `_revinclude`, an entry with `search.mode=outcome` and severity `warning` can mean `suppressed` (caller cannot read an include source) or `incomplete` (page include limit). Keep the primary matches; do not represent the included resources as complete. A failure of a shard *needed for the page* instead fails the whole search. A shard not needed for that page need not be contacted. [Warning payloads][warnings] · [reverse-include tests][includes] · [whole-page failure tests][failures]

## Map the status to an action

| HTTP / FHIR issue code | What the gateway is telling you | Operator action |
| --- | --- | --- |
| **400 `invalid`** | Malformed encoding, invalid value, duplicate control, ambiguous chain target or invalid continuation. An unqualified `GET /{type}/{id}` matching the read route also returns this code, **without authentication or lookup**. [Planner][planner] · [search dispatch][search] · [routes][routes] | Fix request construction; for a read, follow the entry's `fullUrl` or its authorized `alternate`, never guess the owner. |
| **400 `not-supported`** | The request exceeds what the admitted view certifies (for example a parameter, modifier, chain, sort or exact count). A well-formed reference identity filter is unsupported, not malformed. A qualified read accepts no query parameter other than `_format`. [Planner][planner] · [dispatcher][search] · [reads][reads] | Compare the query with [search support](status.md) and the live metadata. Do not silently drop a predicate: the result would answer a different question. |
| **400 `too-costly`** | A work limit, **not elapsed time**: `RequestTooCostlyException`, logical `FetchBudgetExhaustedException`, or a refused chain expansion. Retries and response bytes can consume the budget. [Search][search] · [budget tests][failures] | Narrow the search or bound the chain; inspect calls and bytes before raising a limit. Do not retry the identical request in a loop. |
| **401 `login`** | Protected search or qualified read could not authenticate the caller; the handler challenges with Bearer. **403 `forbidden`** instead means authentication succeeded but the caller is refused: e.g. a missing type grant, SMART scope, denied chain type, or mismatched continuation binding. [Authorization tests][auth] · [replay tests][replay] · [reads][reads] | For 401, verify the federation issuer, audience and token lifetime. For 403, verify the intended grant and identity, not the shard secret. See [credential boundaries](security.md). |
| **404 `not-found`** | A qualified read does not distinguish a disabled/unavailable read binding, unserved type, invalid/unknown alias, or owner's not-found result. Authorization precedes these checks. [Read dispatch][reads] · [eligibility tests][eligibility] | Verify the published read interaction and original owner link. Do not probe other shards for the same ID. |
| **410 `not-found` / `deleted`** | Search continuation no longer valid vs. resource deleted on the owning shard. `ContinuationAnchorMissingException` maps to the former, not a shard 502. [Search][search] · [replay tests][replay] · [read failures][read-tests] | Restart the original query for `not-found`; handle deletion for `deleted`. |
| **413 `too-long` / 414 `too-long`** | POST form body over **32 KiB** (declared or streamed) vs. raw URL query over **8,192 characters** (excluding `?`). The URL check applies even on POST. [Limits and checks][search] · [boundary tests][failures] | For a first search, move predicates into a supported form POST within the body limit. Never split a continuation between URL and body. Check ingress limits separately. |
| **415 `not-supported`** | Non-JSON `_format` or POST without the supported `application/x-www-form-urlencoded` media type and UTF-8 encoding. Invalid UTF-8 *bytes* in an otherwise accepted form are **400 `invalid`**. Metadata and proxy reads also reject unsupported `_format`; duplicate `_format` there is 400 `invalid`. [Search tests][failures] · [metadata][metadata] · [reads][reads] | Request JSON; for POST use a UTF-8 URL-encoded form, not a JSON resource. |
| **500 `exception`** | Explicit distributed-forward strategy contract failure: invalid remaining fetch count or noncanonical returned streams. This is **not** the general mapping for every unexpected exception. [Strategy validation][search] · [read defect tests][read-tests] | Investigate or roll back the registered strategy. Changing the caller's query is not a fix for a broken implementation. |
| **501 `not-supported`** | The planner admits the shape, but this host has no execution path for it (e.g. a required distributed-forward strategy or reverse-include resolver is absent). An uncertified reverse-chain resolution can also take this path. [Search dispatch and refusals][search] | Check host composition and the [support matrix](status.md); distinguish this from a planner 400. Don't install credentials to resolve a missing executor. |
| **502 `exception`** | A needed shard failed or violated its contract; for example a bad cursor, unservable resource, wrong read key or null read result. Exact-total overflow also maps here. Search names the registered alias when known (`unknown` for an unrecognized shard failure); read errors name the **selected** owner, not an alias claimed by an exception. [Shard reasons][reasons] · [search][search] · [read tests][read-tests] | Investigate that owner, service credential and response contract. A backend 401/403 or rejection of an advertised predicate belongs at this boundary, not in a caller challenge. [Design §8][spec] |
| **503 `transient`** | No host configuration/current capability view, signing configuration not ready, required `next` link cannot fit, or the **selected read** binding signals an open circuit. The search handler does not map every shard failure to a circuit-open 503. [Route gates][routes] · [search/link tests][failures] · [read tests][read-tests] | Check readiness, metadata and signing separately. If the next link cannot fit, simplify the original search or correct the limiting configuration; waiting alone won't shrink the link. For a circuit, check the owner before bounded retry. |
| **504 `timeout`** | Request deadline, chain subquery timeout, or a needed shard timeout. A qualified read binding's own cancellation (caller still connected) can mean shard timeout. Deadline diagnostics need not name an owner. [Search][search] · [deadline tests][failures] · [reads][read-tests] | Separate elapsed request time from shard-call latency. For a transient failure, back off and retry the same opaque `next` while valid; do not advance the cursor or substitute partial matches. [Design §8][spec] |

## When the obvious diagnosis is wrong

- **Cold host vs. bad token.** A configured host with no view refuses marked routes before budget, authentication, body parsing or backend calls. With a view, an expired request budget can outrank unavailable signing keys, a bearer challenge or a backend failure. An unconfigured host can still serve green health probes while search returns 503. `/metadata` has its own public view check; it does not test caller authentication. [Route order][routes] · [deadline and cold-host tests][failures] · [metadata][metadata]
- **Invalid vs. stale next link.** Follow the returned URL with GET and `ct` alone. Empty, mixed, malformed, wrong-type, unreadable (including another key ring) or unsupported snapshot data is 400. An intact token for another caller is 403; expiry, a missing replay anchor, or relevant definition drift is 410. An unrelated definition change need not invalidate it. If a successor cannot fit the accepted link limits the host returns 503 **instead of** a truncated page. [Replay tests][replay] · [size tests][failures]
- **Not every HTTP failure has a FHIR body.** Caller disconnect propagates cancellation rather than manufacturing a 504. Unexpected binding defects can reach host error handling. Health checks, unmatched routes and wrong methods are not handled by the FHIR outcome writer; do not assume an ingress rejection, framework 404/405, or generic 500 has `issue[]`. The design's wider route table is not an implemented catch-all. [Startup][startup] · [mapped routes][routes] · [read defect and cancellation tests][read-tests] · [design routes][surface]

## Rollout checks

Use a synthetic caller and non-sensitive fixtures. Record route **template**, status, issue code, configured alias if present, and backend call count; omit bearer tokens, query values, resource IDs and `ct`. Compare observed results with the [search failure suite][failures] and [read failure suite][read-tests].

1. On a ready replica, request a granted type with no token, a denied type with a valid token, and a granted type with duplicate `_count`. Expect 401, 403, and 400 respectively, with no shard work. Repeat the anonymous request while the view is withdrawn: expect 503, not 401. [Authorization][auth] · [planner][planner] · [route gates][routes]
2. Return a synthetic failure then a timeout from a needed test shard. Verify 502 then 504, never a successful partial searchset or a verbatim backend error. Separately signal an open read circuit and verify 503 for that owner. [Page failures][failures] · [read failures][read-tests]
3. Redeem a next link across two identically configured replicas, then try a different caller and an expired link. Expect success, 403 and 410. If cross-replica replay alone gives 400, check key-ring sharing before changing grants. Stop promotion if a forbidden request contacts a shard or any response exposes a backend payload. [Replay suite][replay] · [security rollout checks](security.md)

For client recovery see [Handle errors and expiry](client-errors.md); for further diagnosis see [limits and budgets](limits.md), [authentication](authentication.md), and [deployment and replicas](deployment.md).

[wire]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/SearchOutcome.cs
[search]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[planner]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Search/SearchFailure.cs
[reasons]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/ShardFailureReason.cs
[routes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[reads]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Reads/ProxiedReads.cs
[metadata]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[warnings]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/IncludeOutcomes.cs
[startup]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Program.cs
[failures]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchFailureTests.cs
[includes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[auth]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs
[replay]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[eligibility]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadEligibilityTests.cs
[read-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadFailureTests.cs
[spec]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#L252-L263
[surface]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#L99-L110
