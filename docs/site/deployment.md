---
title: Deploy and validate replicas
description: Configure a cold container, set ingress and probes, and qualify a composed deployment before routing traffic.
sidebar_position: 5
---

# Deploy and validate replicas

## Run the configured image

Build from the repository root. The [Dockerfile][image] publishes a Release build and runs the ASP.NET 11 host as `$APP_UID` on **HTTP 8080**. Stop any [quickstart](quickstart.md) instance first. The following POSIX-shell example supplies [host settings][settings], **not a backend**. Replace the tenant, audience and public URL with deployment values:

```sh
docker build -f deploy/Dockerfile -t ignixa-federation:local .
docker run --rm -p 127.0.0.1:5201:8080 \
  -e Federation__Host__PublicBaseUrl=https://gateway.example.org/fhir \
  -e Federation__Host__Authority=https://login.microsoftonline.com/TENANT-ID/v2.0 \
  -e Federation__Host__Issuer=https://login.microsoftonline.com/TENANT-ID/v2.0 \
  -e Federation__Host__Audience=api://GATEWAY-APP-ID \
  -e Federation__Host__Roles__Federation.Reader__0=Patient \
  ignixa-federation:local
```

Leave `Federation__Backend__Mode` unset. Expect liveness 200 but readiness and metadata 503: no registry or transport is fabricated from the host settings. The authority must be reachable for signing discovery even after a view is supplied. [Cold-host tests][cold] · [Signing checks][signing]. See [binding](binding.md) for what it takes to serve traffic.

## Ingress and probes

On a platform such as Azure Container Apps or AKS, use an image built from the [Dockerfile][image], pin its digest, and target the internal port **8080**. Terminate client TLS at ingress. Set `PublicBaseUrl` to the external HTTPS URL; if that includes `/fhir`, configure ingress to strip that prefix when routing to the application's root. Verify an emitted self or next link **from the client network**: the gateway uses the configured base, not forwarded headers. [URL validation][settings] · [Route paths][routes].

Use `/health/live` for process liveness and `/health/ready` for traffic admission, without bearer tokens. A replica that is alive but lacks a current generation or fresh signing keys should be removed from traffic, not repeatedly restarted. `GET /metadata` independently checks the generation; it is not an authenticated end-to-end probe. [Startup][startup] · [Signing readiness tests][signing] · [Metadata endpoint][metadata].

## Rollout checklist for a composed host

1. **Check every replica directly.** Use the same public base, issuer, audience, grants, ordered registry, options and compatible capability evidence. Configuration for generation building is captured at startup; do not assume editing a file updates an installed generation. Inspect the published metadata and compare it to the intended registry and routes. [Generation state][state] · [Binding checks][dispatch].
2. **Prove that replicas agree on the caller and continuation.** Persist and protect a shared ASP.NET Core Data Protection key ring with the configured application name `Ignixa.Federation`. Using synthetic records, start a small paged search on replica A and redeem its next URL on B with the same caller. Sticky sessions would hide a broken key ring. A relevant plan/registry change may still require a restart of the search. [Data Protection registration][routes] · [Continuation tests][paging].
3. **Prove permissions and ownership.** Exercise one granted and one denied resource type, then verify two equal IDs on different shards produce distinct owner links. Test an eligible alternate read only if the host supplies a read binding. Do not use live patient data for a smoke test. [Authorization tests][auth] · [Response tests][entries] · [Read eligibility tests][reads].
4. **Drain before rollback.** If the candidate fails the checks, keep traffic on the previous image and matching configuration. Retain still-needed keys; do not promise old next links survive an incompatible view. Do not try to solve oversized HTTP tokens by adding Redis: issuance/redemption in this checkout is inline. [Dispatcher][dispatch] · [Continuation tests][paging].

[image]: ../../deploy/Dockerfile
[settings]: ../../src/Ignixa.Federation.Api/Search/SearchHostSettings.cs
[cold]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs
[routes]: ../../src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[startup]: ../../src/Ignixa.Federation.Api/Program.cs
[signing]: ../../test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
[metadata]: ../../src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[state]: ../../src/Ignixa.Federation.Api/Search/PrimarySearchState.cs
[dispatch]: ../../src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[paging]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs
[auth]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs
[entries]: ../../test/Ignixa.Federation.Tests.Integration/Responses/SearchsetResponseTests.cs
[reads]: ../../test/Ignixa.Federation.Tests.Integration/Reads/ProxiedReadEligibilityTests.cs
