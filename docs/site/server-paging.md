---
title: Paging and continuations
description: Operate federation next links across replicas, size inline tokens, and diagnose failed or expired searches.
sidebar_position: 10
---

# Paging and continuations

**The public search endpoint issues inline next links only.** Replicas need the same Data Protection keys and a compatible search configuration, not a shared page cache. If a page needs a next link that the gateway cannot issue or accept, it returns 503 **instead of the page**. Installing Redis does not change that behavior at this revision. [HTTP implementation][route] · [host registration][host] · [design §7.4][spec].

## Follow a page, not a shard cursor

For an admitted paged search, follow the Bundle link whose `relation` is `next`. Send an authenticated **GET to the returned URL unchanged**, even if the first page came from `POST …/_search`. The URL is built from the federation public base, the original resource type and a single `ct` parameter. Do not add `_count`, repeat the filters, or POST the token. If there is no `next`, stop. For client examples see [Page through a result set](client-paging.md); for executable search shapes see the [search support matrix](status.md). [Dispatcher and Bundle construction][route] · [HTTP continuation tests][http-tests].

The token saves **how to resume**, not the results. The protected state includes the original query and method, effective streams and rewrites, page size, replay cursors and their last emitted resource keys, plus dependency revisions. The endpoint replans the saved query, checks today's grants and stream compatibility, then reissues backend requests and resumes after the saved anchor. Resolved distributed-chain rewrites are reused rather than rerunning witness discovery. There are no buffered FHIR resource bodies between requests. Retrying the same inline link does not consume it, but replay against changing shard data is **not** a point-in-time read: a missing anchor means restart, not “skip ahead.” [Search route][route] · [snapshot codec][codec] · [replay tests][http-tests] · [design §§7.3–7.4][spec].

## What protects the link?

- **Encryption and integrity:** The issuer protects a versioned binary envelope with ASP.NET Core Data Protection under the purpose `Ignixa.Federation.Continuations.Inline.v1`, then encodes the ciphertext as canonical unpadded Base64Url. It is neither a JWT nor a plaintext payload with a separate signature. The protector documents the framework default packaging (AES-256-CBC plus HMAC-SHA256); the code calls `IDataProtector`, not a hard-coded cipher or a separately configured continuation signing secret. The bearer token authenticates the HTTP request; its signing key is not the continuation key. [Protector][protector] · [authentication and host registration][host].
- **Caller binding:** A SHA-256 digest binds the validated issuer, subject, object ID (or client ID if no object ID), and ordinally sorted role values. The digest is inside the protected envelope; it is not its signature. Role order does not matter, but changed roles or identity can give 403. Possession of the link is not authorization: the endpoint also rechecks current grants on redemption. [Binding format][binding] · [dispatcher][route] · [authorization and replay tests][http-tests].
- **Handling:** Treat full next URLs as sensitive. Redact `ct`, query strings and authorization headers in proxies, traces, tickets and synthetic checks. The snapshot contains query and replay data even though it does not carry resource bodies or bearer credentials. Record the endpoint, status, time since issue and issuing/redeeming replica instead. [Protector data boundary][protector] · [query frame and replay][route]. See [Security](security.md).

### Replica and rollout checklist

1. Provision a **durable, access-controlled key ring shared by all replicas of this gateway**. Keep the fixed application discriminator `Ignixa.Federation`. Host registration sets the name; it does not provision shared storage. Select the Data Protection key repository and at-rest protection for your environment. Do not share the ring across unrelated environments. [Host setup][host] · [cross-instance and wrong-ring tests][http-tests].
2. Rotate by adding keys while retaining the decryption keys needed by outstanding links. Do not delete the ring on pod replacement. A missing key, different discriminator or wrong purpose fails authentication of the envelope (400), even when the caller presents a valid bearer token. Sticky sessions can hide this mistake until a pod is drained. [Protector][protector] · [redemption tests][redemption].
3. Before draining replica A, obtain a next link on A and follow it through B using the same caller and public URL. Repeat after a restart and across a key rotation. Synchronize clocks and deploy compatible shard registries, definitions, token size limits and effective page sizes. Shared keys solve decryption, **not** replay against a different search plan. [Cross-instance, revision and page-size tests][http-tests] · [protector bounds and clock checks][protector]. See [Deployment and replicas](deployment.md).

## Size and expiration

| `Federation:Tokens` setting | Default | Operational meaning |
| --- | --- | --- |
| `MaxInlineTokenBytes` | 2,048 bytes | Limit on **plaintext envelope**, including metadata, dependencies and framed query/snapshot; not the URL length. The protector allows 256 additional protected bytes when bounding input, hence at most `ceil(4 × (2048 + 256) / 3) = 3072` token characters by default. The gateway separately checks the resulting GET against ingress limits. [Protector][protector] · [dispatcher][route] · [bounds tests][bounds]. |
| `SlidingLifetime` | 15 minutes | Each **successor issued** gets a fresh sliding deadline. Reading a predecessor does not extend it. [Token defaults][options] · [lifetime tests][lifetime]. |
| `AbsoluteLifetime` | 60 minutes | Hard deadline measured from the **first token issued** in the chain; no successor can extend it. The earlier deadline wins; equality is expired. [Token defaults][options] · [protector][protector] · [lifetime tests][lifetime]. |

For example, if the first link is issued at 10:00 UTC, it expires at 10:15. A successor issued at 10:12 expires at 10:27; the first link still expires at 10:15. If the client keeps paging in time and receives another successor at 10:55, that link expires at **11:00**, not 11:10. The host also checks the deadline before returning a staged Bundle so it does not knowingly publish an already expired next link. [Lifetime rules][protector] · [boundary tests on the HTTP route][http-tests].

For sizing, test a multi-page query with your longest public base path and realistic chain rewrites through the **actual ingress**. A 2,100-byte plaintext envelope misses the default limit before encryption, regardless of how few matches the client asked for. Narrow the original search or its resolved key set first. If you raise the inline limit, verify the complete next GET on every hop; increasing it alone cannot raise a proxy's request-line ceiling. See [Limits and budgets](limits.md) and [Configuration binding](binding.md). [Size decision][protector] · [link and ingress checks][route].

## Where Redis fits (and where it does not)

The [design][spec] describes spilling large snapshots to a distributed store. There **is** a tested token service and a native Redis adapter. The public route, however, constructs `ContinuationProtector` and `UnsortedSnapshotCodec` directly; it does not issue or redeem the service's `r1.` references. There is no Redis connection setting to turn HTTP spillover on. Do not diagnose an oversized-link 503 by restarting Redis or assume Redis is an inline paging readiness dependency. [Route construction and issuance][route] · [host registration][host] · [cached token service][cached].

For teams evaluating a separate host integration, the component contract differs from inline paging:

- A protected reference contains a random 32-byte handle, caller binding and timing. The store key uses the handle's SHA-256 digest; the full envelope is separately encrypted and authenticated under a purpose specific to that handle. The default cached **plaintext** ceiling is 256 KiB. This is not an extra allowance on the public route. [Reference and value formats][cached] · [cached issuance tests][cache-tests].
- A cached successor must be established before shortening its cached predecessor's remaining TTL to **at most 60 seconds**. By contrast, issuing a successor on the inline route does not shorten the predecessor. A cache miss is gone; backend unavailability is a distinct component outcome, not an instruction to treat all read errors as expiry. [Cache consistency and outcomes][cached] · [cached successor tests][successors] · [inline lifetime rules][protector].
- The adapter expects a caller-owned StackExchange.Redis `IDatabase`, a standalone primary running Redis 7.0+ and absolute expiry. The host must check server compatibility; the adapter doesn't probe the version. The provider validation note separates observations against a real server from simulated tests and documents the limits of that evidence. [Redis implementation][redis] · [provider validation][redis-tests].

## Triage a failed next GET

| Symptom | What to check first |
| --- | --- |
| **400 invalid**, especially after switching pods | Ensure the client followed the original GET with just `ct` and the correct type. Check URL truncation and key-ring continuity. Wrong ring, malformed or tampered token have the same public result; do not ask for a production token to distinguish them. [HTTP tests][http-tests] · [protector][protector]. |
| **401 / 403** | Authenticate the request first. For 403, compare the caller binding and current read grants, including any witness types. Refreshing credentials is useful only if the caller still has the required identity and access. [Auth and grant checks][route] · [binding tests on the route][http-tests]. |
| **410 Gone** | Restart the original search, not the failed next URL. Compare clocks and elapsed time, then check relevant revision/plan drift or a missing replay anchor. An unrelated definition change alone does not invalidate the link. Do not splice a restarted result set onto an old one as if they were a single snapshot. [HTTP replay tests][http-tests] · [design §7.4][spec]. |
| **503 cannot link next page** | The gateway cannot represent the next state inline or accept the resulting URL; this is not a successful last page. Check the saved query size, rewrites, public path and request-line budget before increasing limits. [Issuance and `NextTooLarge`][route] · [protector bounds][protector]. |

[route]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[host]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[spec]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md
[http-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[codec]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Continuations/UnsortedSnapshotCodec.cs
[protector]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Continuations/ContinuationProtector.cs
[binding]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Continuations/ContinuationBinding.cs
[redemption]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/ContinuationRedemptionTests.cs
[options]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Configuration/TokenOptions.cs
[lifetime]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/ContinuationLifetimeTests.cs
[bounds]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/ContinuationBoundsTests.cs
[cached]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Continuations/ContinuationTokenService.cs
[cache-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/CachedContinuationIssuanceTests.cs
[successors]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/CachedContinuationSuccessorTests.cs
[redis]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Continuations/RedisContinuationCache.cs
[redis-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/RedisContinuationCache.md
