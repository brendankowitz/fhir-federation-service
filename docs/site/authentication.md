---
title: Authenticate callers and grant access
description: Configure JWT discovery, exact issuer and audience validation, app-role grants, and independent backend credentials.
sidebar_position: 8
---

# Authenticate callers and grant access

The caller needs a token **for the gateway**, not a credential for each shard. The gateway authenticates one caller identity and authorizes access by resource type. A backend client uses the federation's own credentials; its factory receives the capability view and request budget, never the caller's bearer token. [Caller validation][jwt] · [Grant evaluation][grants] · [Backend trust boundary][factory].

## Configure the gateway audience

| `Federation:Host` key | What to supply and verify |
| --- | --- |
| `PublicBaseUrl` | External HTTPS base for generated links. It is **not** the JWT audience or the internal container listener. [Host settings][settings] |
| `Authority` | Trusted HTTPS discovery URL, without user information, query or fragment. Discovery identifies the HTTPS signing-key endpoint. [JWT registration][jwt] · [Signing configuration][keys] |
| `Issuer` | The exact expected `iss`. The issuer advertised by metadata does not widen this value. [JWT tests][jwt-tests] |
| `Audience` | The gateway's exact token audience. Case and trailing slash matter. Signatures, expiry and lifetime are validated, with one minute of clock skew. [JWT registration][jwt] · [JWT tests][jwt-tests] |
| `Roles:<role>:<index>` | Map an exact app-role value to each permitted resource type. For example, `Federation__Host__Roles__Federation.Reader__0=Observation` grants Observation; add `…__1=Patient` only if the role should read Patient too. [Host settings][settings] · [Grant evaluation][grants] |

**A role grant covers all resources of the granted type on every shard.** The gateway unions grants for exact raw `roles` claim values; it does not translate SMART scopes into row-level permissions. A SMART `patient/`, `user/`, `system/` or `launch…` scope in `scp` or `scope` is refused even if a valid role is present. Do not use this model where a caller must see only one patient's compartment. [Caller access rules and tests][access-tests] · [Security design §10][design].

## Verify grants on a composed host

Use a synthetic principal and records. First check `GET /metadata` and `/health/ready`. Metadata is public and its success alone does not prove that signing is ready. Signing refresh targets five minutes, with a ten-second fetch timeout and one-hour maximum age for last-good keys. If signing is unavailable, protected requests may return **503 before token validation**, not 401. [Metadata endpoint][metadata] · [Signing service][keys] · [Readiness tests][ready].

| Test | Expected behavior |
| --- | --- |
| Search without a token, or with wrong issuer, audience or signature | 401 on an otherwise ready host; do not broaden type grants to fix invalid credentials. [Authentication tests][jwt-tests] |
| Valid Observation-only role searches Patient | 403, before a shard call. A valid token is not itself permission to search. [Authorization tests][authorization] |
| Observation-only role searches `Observation?subject:Patient.name=Smith` | The joined Patient type also needs a grant. A missing target grant is forbidden, not an empty match set. [Forward-chain tests][forward] |
| A permitted primary search requests a reverse include from a denied source type | The primary page can be returned; the denied includes are omitted with a generic outcome rather than leaking those resources. [Reverse-include tests][include] |
| Caller follows a `next` URL or owner-qualified alternate | Replay rechecks caller binding and current grants; a proxy read requires the resource-type grant and an eligible binding. Share neither tokens nor next URLs between users. [Continuation tests][paging] · [Read eligibility tests][read] |

For a backend permission failure, check the shard's audience and read-only identity separately. Do not forward the caller token or grant direct access to all owners as a workaround. See [Shard registry](shards.md) and the [transport contract][factory].

[settings]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/SearchHostSettings.cs
[jwt]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Authentication/FederationCallerAuthentication.cs
[keys]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Authentication/SigningConfiguration.cs
[jwt-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Authentication/FederationCallerAuthenticationTests.cs
[grants]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Authorization/CallerAccess.cs
[access-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Authorization/CallerAccessTests.cs
[factory]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/IShardClientFactory.cs
[design]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md
[metadata]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[ready]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
[authorization]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs
[forward]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchForwardChainTests.cs
[include]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[paging]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[read]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadEligibilityTests.cs
