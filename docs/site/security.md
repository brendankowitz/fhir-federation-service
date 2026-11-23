---
title: Secure a federation gateway
description: Decide who may search across shards, confine each credential to its owner, and verify those boundaries before admitting traffic.
sidebar_position: 14
---

# Secure a federation gateway

**Grant a federation role only if its holder may read every resource of each granted type on every participating shard.** A `Patient` grant is not a grant to read *one* patient. If the deployment needs consent, compartment, tenant or record-level restrictions within that population, do not expose it through that role. Adding a shard changes what existing role holders can see; review it like a new grant. [Security design, §10][design] · [grant evaluator][grants]

This guide covers `af1407f`, including the authenticated search API from #17. There are two separate release decisions: the API enforces caller access; the operator must supply and verify the backend that uses the gateway's authority. The [stock host][host] and [shard project][shards] do **not** include a production shard HTTP transport. Registry validation is not evidence that a secret was resolved, a shard accepted a token, or a backend link was safe to follow. [Search composition][search] · [cold-host tests][cold]

## Which credential goes where?

| Request | Credential and destination | Operator decision |
| --- | --- | --- |
| Caller searches, follows `next`, or uses a gateway `alternate` read link | Caller token for the **federation**. Generated gateway links use `Federation:Host:PublicBaseUrl`, not the incoming `Host` or forwarding headers. [Host settings][settings] · [client confinement tests][client] | Publish the external HTTPS base including its path prefix. Route it to this gateway; do not let clients attach a default bearer to arbitrary Bundle links. |
| Gateway searches or reads an owner shard | The **gateway's read-only identity for that shard**. No caller-token passthrough. This is the [backend contract][design], not a feature to infer from the stock image. | Verify the actual transport, its token audience and its egress rules before granting it production access. |
| Caller reads `entry.fullUrl` directly | The **client's own shard identity**, subject to shard authorization. `fullUrl` discloses the owner URL by design; it is neither a capability nor a gateway URL. The sample uses a separate shard transport and does not substitute a federation token when a shard token is absent. [Data handling design][design] · [client confinement tests][client] | Configure direct reads only for approved shard bases. If clients cannot reach a shard or have no shard grant, offer an authorized proxy read or no read at all. |

## Admit the caller before using shard authority

Under `Federation:Host`, set `Authority` to trusted HTTPS discovery, `Issuer` to the exact accepted issuer and `Audience` to the federation application, **not** a shard. Discovery supplies keys; its advertised issuer does not widen the issuer pin. The named bearer scheme validates signature, expiry and lifetime (one minute of skew); audience matching is case-sensitive with no trailing-slash equivalence. Keep replica clocks synchronized and verify discovery and key refresh after an identity-provider change. [Bearer configuration][jwt] · [authentication tests][jwt-tests] · [signing readiness tests][signing]

Map an exact app-role value to its readable types, for example `Federation__Host__Roles__Federation.Reader__0=Patient`. The gateway takes the union of matching raw `roles` values from one authenticated identity. `scp` and `scope` do not grant access: any whitespace-delimited scope starting with `patient/`, `user/`, `system/` or `launch` (case-insensitive) refuses the caller even if a role matches. On a ready host, missing or invalid authentication yields **401**; a valid identity without a usable grant or with a SMART scope gets **403**, before shard work. Do not fix a 403 by stripping scopes at ingress. Issue the right token or refuse access. [Host settings][settings] · [grant evaluator][grants] · [route tests][auth-tests]

Check indirect access as well as the primary type:

- A forward chain requires the target type; `_has` requires the witness type. Denial is **403**, not an empty result that could be mistaken for “no patients matched.” These grants are checked again on replay. [Forward-chain tests][forward] · [reverse-chain tests][reverse]
- For a supported `_revinclude`, a denied source is omitted with a generic outcome; the authorized primary results remain. Do not grant the source just to remove the warning. [Reverse-include tests][includes]
- A qualified `/shards/{alias}/{type}/{id}` read checks caller access **before** proxy eligibility. Serving it needs an enabled read binding and a published readable type; the binding receives the registry, key and budget, not the caller's bearer. A successful read returns the resource bytes with the shard URL in `Content-Location`. Disable `ProxyReads` if this path is unnecessary. [Read implementation][reads] · [eligibility tests][eligibility]

## Confine the gateway's outgoing requests

For each `Federation:Shards` entry, record the owner, canonical HTTPS `BaseUrl`, each approved `LinkBaseAliases` value, token resource and read-only grant. The registry checks canonical bases and rejects overlapping ownership across shards. `Auth` supports `ManagedIdentity` with `Resource` and optional `ClientId`, or `ClientCredentials` with `TenantId`, `ClientId`, `Resource` and `SecretName`. The last field names a secret; never put its value there. Prefer a managed identity where supported; otherwise restrict secret-store access to the owning workload and rehearse rotation. [Registry credential tests][registry-auth] · [base and alias tests][bases]

For a supplied transport, make [§10.2][design] an acceptance condition: before adding credentials to **each** outgoing request, including backend paging, match scheme, host, effective port and base **path segment boundary** to the selected shard's base or approved alias. Another registered shard is still the wrong owner. Reject ambiguous paths and foreign links; disable automatic redirects rather than retrying at `Location`. Restrict network egress independently to approved destinations and review alias/DNS changes with the shard owner. Do not copy inbound authorization headers into a shared HTTP client. For clients, the [sample confinement tests][client] demonstrate separate handlers, rejection before send and redirects disabled; they do not certify your gateway transport.

## Protect paging, logs and audit data

Treat the complete `next` URL as sensitive. The HTTP search path issues inline Data Protection state, not a durable session in Redis. Redemption binds it to the validated issuer, subject, object ID (otherwise client ID) and ordinally sorted role values; possession is not authorization. Persist a protected key ring shared **only** by replicas of the same deployment, retain old keys through the allowed replay lifetime, and keep the application name and admitted configuration consistent. A different key ring can cause **400**, a different caller **403**, and expiry or relevant definition drift **410**. For 410, restart the original search instead of editing the link. [HTTP dispatcher][search] · [Data Protection registration][routes] · [binding][binding] · [replay tests][replay]

Keep tokens, authorization headers, full query strings, continuation URLs, resource IDs and bodies out of ordinary logs, traces, metric labels and support tickets. Apply redaction at ingress and on clients as well as at the API. The [audit design][design] calls for a separate protected sink with caller, parameters, shards, counts and outcome, **not resource content**. Search parameters themselves can identify a patient. Restrict readers, encrypt storage, set retention and test deletion. The [stock startup][host] does not install that sink. The design also calls for `traceparent` and an opaque `X-Federation-Caller`; an [identifier derivation helper][correlation] is not proof that a transport sends either. Never trust a client-supplied correlation header as identity or use a correlation ID to authorize a read.

## Before routing real traffic

Run these checks in an isolated staging deployment with synthetic identities and non-sensitive resources. Record status, shard alias and whether a call occurred, **not** token values or payloads.

| Gate | Evidence to require |
| --- | --- |
| **Host and ingress** | Check `/health/live`, `/health/ready`, and `/metadata` separately without credentials. A default green probe is not backend certification. On the configured host require a current capability view and fresh signing keys, then actually search. Metadata is public; a metadata response alone does not exercise caller signing. Confirm emitted links resolve at the intended external base. [Startup][host] · [route wiring][routes] · [metadata][metadata] · [signing tests][signing] |
| **Grant matrix** | Exercise a permitted type and a denied one. Repeat with no token, wrong audience, and a SMART scope plus an otherwise valid role. Confirm the refusals above and zero backend calls. Verify denied chain, reverse-include and proxy cases separately; a successful primary search proves none of them. [Authorization][auth-tests] · [chains][forward] · [includes][includes] · [reads][eligibility] |
| **Outbound identity** | With a controlled shard stub, verify only the expected read identity reaches each owner; raw caller tokens never do. Use a stub-provided off-base next link and a redirect and require **no request** to their destination. Verify the client separately for federation `next`/`alternate` and direct `fullUrl`. If the shard rejects the service credential, investigate the gateway-to-shard grant, not the caller role: the design maps backend 401/403 to 502. [Backend failure contract][failure-design] · [confinement contract][design] · [client tests][client] |
| **Replica change** | Obtain a first page on replica A and redeem its exact next URL on B with the same identity. Try another caller and a withdrawn type grant; confirm refusal before shard work. Repeat across a restart and signing-key rotation. [Replay tests][replay] · [signing tests][signing] |
| **Disclosure and rollback** | Inspect staging ingress, app, shard and exporter output for synthetic markers; verify the protected audit record separately. Stop promotion for any credential at the wrong destination, unauthorized shard work, or sensitive data in ordinary logs. Drain the revision, restore the last approved image and configuration, and revoke or rotate exposed credentials before retrying. |

For the rollout, use [Authentication and authorization](authentication.md) to configure the role matrix, [Register and certify shards](shards.md) to check each owner, and [Deployment and replicas](deployment.md) to test the published host. For client credential handling see [Follow resource links](client-reads.md). Use [Search support](status.md) to select a supported test query and [Errors](errors.md) to distinguish caller refusal, unavailable dependencies and backend failures.

[design]: ../superpowers/specs/2026-11-19-fhir-federation-service-design.md#L284-L305
[failure-design]: ../superpowers/specs/2026-11-19-fhir-federation-service-design.md#L250-L263
[host]: ../../src/Ignixa.Federation.Api/Program.cs
[shards]: ../../src/Ignixa.Federation.Shards
[cold]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs
[search]: ../../src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[routes]: ../../src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[metadata]: ../../src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[settings]: ../../src/Ignixa.Federation.Api/Search/SearchHostSettings.cs
[jwt]: ../../src/Ignixa.Federation.Api/Authentication/FederationCallerAuthentication.cs
[jwt-tests]: ../../test/Ignixa.Federation.Tests.Integration/Authentication/FederationCallerAuthenticationTests.cs
[signing]: ../../test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
[grants]: ../../src/Ignixa.Federation.Api/Authorization/CallerAccess.cs
[auth-tests]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs
[forward]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchForwardChainTests.cs
[reverse]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchReverseChainTests.cs
[includes]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[reads]: ../../src/Ignixa.Federation.Api/Reads/ProxiedReads.cs
[eligibility]: ../../test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadEligibilityTests.cs
[registry-auth]: ../../test/Ignixa.Federation.Core.UnitTests/Shards/ShardRegistryAuthTests.cs
[bases]: ../../test/Ignixa.Federation.Core.UnitTests/Shards/ShardRegistryBaseUrlTests.cs
[client]: ../../test/Ignixa.Federation.SampleClient.UnitTests/CredentialConfinementTests.cs
[binding]: ../../src/Ignixa.Federation.Api/Continuations/ContinuationBinding.cs
[replay]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[correlation]: ../../src/Ignixa.Federation.Api/Correlation/CallerCorrelation.cs
