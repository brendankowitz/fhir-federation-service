---
title: Troubleshoot by symptom
description: Check the failing gateway boundary, recover safely, and verify the fix with synthetic searches.
sidebar_position: 15
---

# Troubleshoot by symptom

Start with one replica, a known synthetic query and the route that failed: **first page, `next`, owner read, metadata, or health probe**. Record the HTTP status and FHIR `issue[].code` if present. Leave tokens, complete URLs, patient IDs, request bodies and backend payloads out of incident tickets. A status alone is ambiguous: for example, a 410 on `next` and a 410 on an owner read require different actions. [Search dispatcher][search] · [owner read handler][reads] · [redaction guidance][privacy]

## “The container is healthy, but every search returns 503”

- [ ] Call `/health/live`, `/health/ready` and `/metadata` separately on the affected replica. Both probes green with metadata unavailable? Check that the running process actually received `Federation:Host`. In the unconfigured host, green health does not establish that search was configured. Compare with the [quickstart](quickstart.md) before debugging a shard. [Startup][program] · [route setup][routes]
- [ ] Live but not ready, and metadata also returns 503? Check whether a **current usable view** was installed. A missing, withdrawn or no-longer-eligible view can refuse requests before authentication or any backend connection. In manual publication mode, verify that the host supplied the transport and that publication succeeded. In `PrototypeCatalog` mode, verify that the host registered an admission source; a missing source is a startup error, not a warm-up delay. Adding URLs alone does not install these components. [Registration][routes] · [publication tests][publication] · [cold-host tests][cold]
- [ ] Metadata succeeds but protected routes return a signing-unavailable outcome? Check authority reachability from the replica, discovery issuer and signing-key availability. Restore discovery and wait for fresh keys; do not disable token validation or restart healthy replicas to chase a readiness failure. Confirm with a protected synthetic search, not just metadata. [Signing recovery tests][signing] · [authentication](authentication.md)

If the process **never starts**, compare [binding](binding.md) and [configuration](https://brendankowitz.github.io/fhir-federation-service/configuration/) with the settings actually supplied. Distinguish invalid mode, missing required service and invalid options when constructing a warm view from an intentionally cold host. Restart with corrected settings rather than assuming a later publication rereads startup settings. [Route composition][routes] · [publication and settings tests][publication]

## “Metadata works; the same caller gets 401 or 403”

- [ ] First distinguish a **gateway route** from a direct URL on a shard. On a gateway search, 401 `login` is a bearer authentication failure. Check token issuer, audience, validity interval and signing keys using approved tooling; never paste the token into a ticket. On a direct shard link, use credentials intended for that shard, not the federation token. [Authorization tests][auth] · [signing tests][signing] · [credential boundaries](security.md)
- [ ] For 403, confirm the caller's configured role/type grants and required scopes. Test an ordinary search of a granted type first; then add the chain or reverse chain. Authorization for the primary type alone does not authorize the witness type. Do not solve a 403 by increasing backend service-account privileges. [Authorization tests][auth] · [search dispatch][search]
- [ ] If only `next` fails, compare the validated caller and roles with those on page one. Retry with the intended caller and current grants, not a token copied from another session. [Replay tests][replay]

## “A basic search works, but my filter, count or sort doesn't”

- [ ] Separate **400 `invalid`**, **400 `not-supported`**, **400 `too-costly`** and **501 `not-supported`** before changing configuration. With synthetic values, start with an admitted type and add one occurrence at a time. Check encoding, repetition and modifiers; don't remove a production predicate and present the result as equivalent. [Search handler][search] · [failure tests][failure]
- [ ] For an unsupported shape, compare the current metadata and the [support matrix](status.md). A planner refusal for lack of proof is different from a valid plan with no HTTP executor. For example, a certified sort still does not imply sorted HTTP paging. Exact count requires its own proof and shard count dialects; paged `_total=accurate` is not a substitute for count-only. [Dispatcher][search] · [count tests][count]
- [ ] For 413/414/415, check the request at ingress **and** the gateway. For an initial search, try a UTF-8 form POST to `/{type}/_search`; sending JSON is not a workaround. Never convert an opaque next link into a POST. [Ingress cases][failure] · [continuation tests][replay]

## “The first page worked, then paging broke”

| Symptom | Check in order |
| --- | --- |
| 400 on the next GET | Follow the returned link unchanged: same type, `ct` only. If failures follow replica turnover rather than a particular client, test issuance on A and redemption on B. Check shared Data Protection keys and application name before changing JWT settings. [Route registration][routes] · [replay tests][replay] |
| 403 on `next` | Check identity, roles and grants; decryption alone does not authorize replay. [Replay tests][replay] |
| 410 `not-found` on `next` | Restart the original search. Check age against both token lifetimes, relevant definition changes, and a missing replay anchor. Save neither shard cursors nor offsets as substitutes. [Continuation tests][replay] · [lifetime tests][lifetime] |
| 503 instead of a page with `next` | If readiness is green, check whether the successor exceeded inline or accepted query size. Reproduce with fewer synthetic predicates/streams; separate that result from a signing or capability outage. Public paging does not spill oversized state to Redis in this checkout. [Dispatcher][search] · [failure tests][failure] |

Confirm recovery by following two successive pages across replicas. See [server paging](server-paging.md) before rotating keys or changing token sizes.

## “Search times out, costs too much, or names a failing shard”

- [ ] **400 `too-costly`:** separate work from time. Compare granted attempts (including retries and backend next follows), consumed bytes, and chain expansion with the relevant [limit](limits.md). A small `_count` is not a ceiling on witness discovery or backend response size. Narrow the synthetic query before considering a global increase. [Request budget][budget] · [dispatcher][search]
- [ ] **504 `timeout`:** compare request duration with the overall deadline, then inspect time spent in individual calls and subqueries. Verify cancellation and retry behavior in your binding. Increasing the request deadline will not correct a shard that refuses a supported predicate. [Budget][budget] · [failure tests][failure]
- [ ] **502 `exception`:** isolate the named alias if available; check the binding's service credential, response identity/shape, paging link and strict-search contract using synthetic data. For an owner read also check for a mismatched returned key. A necessary backend failure fails the operation; do not turn it into an empty or partial success. Restore and retest that owner before returning it to service. [Failure tests][failure] · [read failure tests][read-tests] · [shard certification](shards.md)

## “An entry is missing, duplicated, or unreadable”

- [ ] Inspect `entry.search.mode` before counting results: a reverse include can contribute `include` entries or a warning `outcome`. Suppression for a denied include type is different from truncation at the include cap. Do not widen grants merely to eliminate a warning. [Reverse-include tests][includes]
- [ ] Compare **owner plus type plus ID**, not bare IDs. Follow the Bundle's entry links; a bare `/{type}/{id}` read is deliberately ambiguous. For a qualified 404 check the read binding, type, selected alias and actual owner record. A 410 `deleted` on a read is not token expiry. Do not probe other owners for the same ID. [Read dispatch][reads] · [search response tests][bundle]
- [ ] For intermittent behavior at rollout, compare the affected replicas' view, public base, grants, signing readiness and continuation keys. A request already in flight may finish on its captured generation after withdrawal; a new request must observe the new state. Verify with a fresh synthetic request before declaring a stale-view incident. [Publication tests][publication] · [replay tests][replay]

For escalation, attach the revision, replica, time window, route template, status/issue code, affected alias if disclosed, the three probe results, and whether the same synthetic request succeeds on a second replica. Include sanitized counts and durations rather than a raw trace. See [observability](observability.md) for which signals require host instrumentation and [errors](errors.md) for the full response map.

[program]: ../../src/Ignixa.Federation.Api/Program.cs
[routes]: ../../src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[search]: ../../src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[reads]: ../../src/Ignixa.Federation.Api/Reads/ProxiedReads.cs
[budget]: ../../src/Ignixa.Federation.Api/Budgets/RequestBudget.cs
[cold]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs
[publication]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchCapabilityPublicationTests.cs
[signing]: ../../test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
[auth]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs
[replay]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[lifetime]: ../../test/Ignixa.Federation.Tests.Integration/Continuations/ContinuationLifetimeTests.cs
[failure]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchFailureTests.cs
[count]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchCountTests.cs
[read-tests]: ../../test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadFailureTests.cs
[includes]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[bundle]: ../../test/Ignixa.Federation.Tests.Integration/Responses/SearchsetResponseTests.cs
[privacy]: ../guides/CallerCorrelation.md
