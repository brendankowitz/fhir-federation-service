---
title: Operate a gateway
description: Deploy and probe the gateway, configure caller access and shard identity, and diagnose readiness, search, and paging failures.
sidebar_position: 2
---

# Operate a gateway

**Start by identifying which host you have.** This checkout ships the read-only FHIR R4 API from [#17](https://github.com/brendankowitz/fhir-federation-service/pull/17), but the stock executable does not connect a verified shard backend. A green probe on the default image is a container smoke test, not approval to send it FHIR traffic. See [Search support](status.md) for the query shapes exercised on a composed host and the [generated configuration reference](configuration.md) for option names and defaults.

## Bring up a replica

From the repository root, run the local stack and check three paths:

```sh
docker compose -f deploy/compose.yaml up -d --build
curl -i http://127.0.0.1:5201/health/live
curl -i http://127.0.0.1:5201/health/ready
curl -i http://127.0.0.1:5201/metadata
```

Expect **200, 200, 503**, respectively. The [Compose stack] maps loopback port 5201 to HTTP 8080 in the non-root [runtime image]. Its two BusyBox `httpd` containers are placeholders, not FHIR servers. Inspect with `docker compose -f deploy/compose.yaml logs federation`; stop with `docker compose -f deploy/compose.yaml down`. Without Docker, run `dotnet run --project src/Ignixa.Federation.Api --urls http://localhost:5201` with the SDK selected by `global.json`.

To exercise **configured but cold** startup, build the gateway alone and run the following in a POSIX shell. Replace the example tenant, audience and public base with your own values; the local HTTP port is only for probes. The public base is the **external HTTPS address**, not the internal listener.

```sh
docker build -f deploy/Dockerfile -t ignixa-federation:local .
docker run --rm -p 127.0.0.1:5201:8080 \
  -e Federation__Host__PublicBaseUrl=https://gateway.example.org/fhir \
  -e Federation__Host__Authority=https://login.microsoftonline.com/TENANT-ID/v2.0 \
  -e Federation__Host__Issuer=https://login.microsoftonline.com/TENANT-ID/v2.0 \
  -e Federation__Host__Audience=api://GATEWAY-APP-ID \
  -e Federation__Host__Roles__Federation.Reader__0=Patient \
  -e Federation__Host__Roles__Federation.Reader__1=Observation \
  ignixa-federation:local
```

Leave `Federation__Backend__Mode` **unset** in this example. From another terminal repeat the three probes: expect **200, 503, 503**, even with a reachable identity provider. A valid `Federation:Host` enables signing-key retrieval; it does not provide a capability view. A partial host section fails startup rather than falling back to unconfigured mode. [Host settings], [cold-host tests].

| Host state | What readiness and routes tell you |
| --- | --- |
| No `Federation:Host` (shipped defaults) | Liveness runs no checks. Readiness selects checks tagged `ready`; by default there are none. Type search, metadata and qualified reads answer 503. |
| Configured, no current view | Process stays live; `search-capabilities` is not ready. Metadata and capability-dependent routes refuse with 503 before shard work. Binding and validation of the search options wait for a view; a listening process does not certify the registry. |
| Configured, composed with a view and transport | Readiness needs both a current search generation and fresh `caller-signing` configuration. Metadata is public and depends on the generation, **not** on caller signing readiness. Search and qualified reads require both, then authenticate the caller. A missing transport or invalid registry for a supplied startup view prevents startup. |
| Explicit `Federation:Backend:Mode=PrototypeCatalog` | Requires valid host settings and a registered `ICapabilityAdmissionSource`. A single worker starts cold, prepares verified generations and withdraws stale ones. No manual publisher or seed view is used in this mode. The stock executable lacks that source and fails **before listening**; adding shard URLs cannot warm it. |

The [host startup], [route registration], [catalog integration tests] and [signing readiness tests] cover these distinctions. For a composed deployment, send probes without bearer tokens. Route traffic only to ready replicas; a cold or stale dependency generally calls for removing the replica from traffic, not killing a live process.

## Configure the two trust boundaries

**Caller → federation.** `Federation:Host:Authority` is the HTTPS discovery authority. `Issuer` and `Audience` pin the accepted token values; an audience differing only by case or trailing slash is not equivalent. Use `Roles:<exact-app-role>:<index>` for the union of readable FHIR types. A `Patient` grant allows **all Patient resources on every shard**, not a patient compartment. Tokens with SMART `patient/`, `user/`, `system/` or `launch…` scopes are refused even with a matching role. Missing or invalid tokens get 401; authenticated callers without a usable grant get 403. [JWT validation tests], [grant evaluation].

Check grants for the primary type **and** forward targets or reverse-chain witnesses. A denied join cannot be treated as “no matches.” A denied reverse-include source is instead omitted with an outcome. Qualified reads and continuation replay check access again. Links use `PublicBaseUrl`, not the request `Host` or forwarded headers. If ingress exposes `/fhir`, route that external prefix to the application's root so clients can actually follow emitted links.

**Federation → shard.** Configure `Federation:Shards` as an ordered registry: stable `Id`, URL-safe `Alias`, canonical HTTPS `BaseUrl`, backend `Profile`, per-shard `Auth`, and optional `LinkBaseAliases`. For example, `Federation__Shards__0__BaseUrl` is the first owner's base. Do not use the Compose HTTP placeholders in a real registry. Managed identity settings name the target `Resource` and optionally a user-assigned `ClientId`; client credentials name `TenantId`, `ClientId`, `Resource` and `SecretName` (a reference, not a secret). [Registry validation], [credential configuration tests].

Give each gateway identity only the required shard read access. Never relay the caller token to the shard; trust only registered HTTPS bases and authorized aliases, not arbitrary links or redirects. These are **requirements for the shard transport**, not evidence that this image obtains Azure credentials or validates backend capabilities. The production backend proposed in [#19](https://github.com/brendankowitz/fhir-federation-service/pull/19) is not in this checkout.

## Place resources and route reads

- A relative `Patient/42` resolves on the referencing resource's shard. A cross-shard reference needs an absolute URL under the target's registered base or alias. Do not follow foreign absolute references. Duplicate type/ID pairs on different owners are distinct, not replicas to deduplicate.
- Keep shard order stable: it drives unsorted emission. `ChainResolution:Default` is `Distributed`. Use a `Passthrough` override only for a path whose references really are local and whose native search is certified. A misplaced resource can otherwise disappear from results. [Design §§4, 7][design].
- `entry.fullUrl` names the shard; the caller needs separate access to read it directly. With `ProxyReads`, an `IShardReadBinding` and an eligible type, use the alternate `/shards/{alias}/{type}/{id}` read. It returns the resource without rewriting references and identifies the owner in `Content-Location`. `/{type}/{id}` is ambiguous and returns 400; the gateway does not probe all owners. [Read implementation].

## Size and protect a request

The [request budget] belongs to **one incoming request**. Primary fetches, witness discovery, count and include stages spend from the same allowance, not from a fresh budget at each stage. The host relies on the composed transport to meter physical sends and response bytes.

| Default or boundary | Operational consequence |
| --- | --- |
| Page size 20, maximum 100 | `_count` limits primary matches; includes do not consume match slots. A small page does not make a broad witness scan cheap. |
| `BackendCallsPerShard` 40; aggregate responses 64 MiB | Two shards allow 80 calls **in total**, not 40 reserved for each. Account for retries and cursor fetches. A required shard failure does not yield a partial successful searchset. |
| 16 parallel backend calls; request 30 s, backend call 10 s, chain subquery 15 s | Stage timeouts fit inside the request deadline. Concurrent requests multiply backend load. The shard-count warning threshold (16) is not a hard cap or benchmark. |
| 1,000 chain keys; 5,000 reverse-chain source rows; 1,000 includes per page | Narrow the witness predicate before raising limits. Include overflow is signaled by an incomplete outcome; it does not truncate primary matches. |
| Raw URL query at most 8,192 characters; form body at most 32 KiB | Use UTF-8 `application/x-www-form-urlencoded` for POST search. Overlong query/body gives 414/413. POST cannot be used to redeem a continuation. [HTTP dispatcher]. |

## Keep continuations usable across replicas

Follow the opaque `next` URL with GET, the same identity and no added parameters. The [HTTP dispatcher] uses ASP.NET Core Data Protection with application name `Ignixa.Federation`; persist and protect a shared key ring across replicas, restarts and rollouts. Affinity is not a substitute for durable keys. Default sliding/absolute lifetimes are 15/60 minutes. Keep public base, registry, profiles and admitted parameter definitions consistent across replicas. Replay checks the saved plan, relevant revisions, current grants and page anchor. Do not log the token or its URL.

**Redis is not an HTTP fallback in this build.** The [cache provider and interop notes] describe a separately tested Redis 7.0+ standalone-primary adapter and cache-capable token service. The route instead issues and redeems inline state: if the successor exceeds the configured inline bound (default 2,048 bytes) or would not fit the server's ingress limits, the page fails with 503. Adding a Redis endpoint does not fix it or add a readiness check. Check proxy request-line/header limits separately. The opt-in Redis test variables are **not** deployment settings. [HTTP continuation tests].

## Deploy, observe and recover

For an Azure Container Apps or Kubernetes smoke deployment, push your own [runtime image] to a registry, select its immutable digest, target port **8080** and use HTTP probes `/health/live` and `/health/ready` there. Terminate external TLS at ingress. Start privately; the default image's green readiness is not a shard test. Before serving real data, require a composed backend, read-only shard identities, network reachability to the issuer and each shard, protected key storage and a tested authorization policy. With a synthetic caller and non-sensitive records, verify both a permitted search and a denied type, then follow a next link across two replicas. On rollout failure drain the new revision and restore the prior image/configuration together; changed keys or registry definitions may require clients to restart searches.

The [telemetry definitions] expose an `ActivitySource` and `Meter` named `Ignixa.Federation`; the [shard span helper] emits `shard {stage}` with `federation.shard.alias` and `federation.stage` when a listener is present. The stock host does not configure an OpenTelemetry exporter or a protected audit sink. In a deployment, monitor readiness transitions separately from liveness, then request latency, backend calls/bytes by shard and stage, budget refusals and continuation failures. The [design, §14][design] describes intended telemetry, not dashboards shipped with the image. Keep identifiers, raw query strings, resource bodies and bearer/continuation tokens out of ordinary logs and metric labels; protect any audit data separately.

| Symptom | First action |
| --- | --- |
| Cannot connect, even to `/health/live` | Verify internal port 8080, loopback mapping and startup logs. Check invalid `Federation:Host` keys or explicit catalog mode without a source. |
| Metadata 503; liveness green | Determine whether the host is unconfigured, cold or has withdrawn its generation. Fix composition/evidence first; changing caller roles cannot produce a capability view. |
| Metadata 200, search 503 | Check `caller-signing`, issuer discovery, HTTPS key retrieval and clock. Signing refresh targets five minutes; last-good keys age out at one hour. A green metadata response is not a signing check. [Signing key refresh]. |
| Search 401/403 | Verify signature, lifetime, exact issuer/audience, SMART scope refusal, type roles and a stable caller identity. Do not grant the caller direct backend credentials to fix a gateway grant. |
| Search 400 `not-supported` or 501 | Compare capability evidence and method/shape against the [matrix](status.md). A planner refusal differs from a valid plan with no bound execution path. |
| 400 `too-costly` or 504 | Reduce scan breadth, fan-out or page size for deterministic limits; for a deadline locate the slow stage. Raising a timeout does not increase the call/byte budget. |
| 502 from a needed shard | Check backend identity, expected response shape and strict query handling. Do not treat the other shards' rows as a complete response. |
| Next link rejected | Malformed or unprotectable token: 400 (check key ring); caller binding mismatch: 403; expired, relevant drift or missing anchor: 410 (restart the original search). For a transient transport error retry the same link, not a guessed shard cursor. |

[Compose stack]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/deploy/compose.yaml
[runtime image]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/deploy/Dockerfile
[host startup]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Program.cs
[Host settings]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/SearchHostSettings.cs
[route registration]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[cold-host tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs
[catalog integration tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/CatalogBackendHostTests.cs
[signing readiness tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
[JWT validation tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Authentication/FederationCallerAuthenticationTests.cs
[grant evaluation]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Authorization/CallerAccess.cs
[Registry validation]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Shards/ShardRegistry.cs
[credential configuration tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Core.UnitTests/Shards/ShardRegistryAuthTests.cs
[Read implementation]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Reads/ProxiedReads.cs
[request budget]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Budgets/RequestBudget.cs
[HTTP dispatcher]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[cache provider and interop notes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Continuations/RedisContinuationCache.md
[HTTP continuation tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[telemetry definitions]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Diagnostics/FederationTelemetry.cs
[shard span helper]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Shards/Diagnostics/ShardActivity.cs
[Signing key refresh]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Authentication/SigningConfiguration.cs
[design]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md
