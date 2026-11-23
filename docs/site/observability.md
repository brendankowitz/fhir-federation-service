---
title: Observability
description: Distinguish liveness from serving readiness, collect the available federation signals, and verify a deployment without recording patient data.
sidebar_position: 13
---

# Observability

Use three checks for three different questions: **is the process answering, does this replica have the state to serve traffic, and can a caller complete a search?** Neither a green probe nor a successful metadata request answers all three. The [host pipeline][program], [route registrations][routes] and [metadata handler][metadata] implement different checks.

## Probe a replica before putting it in rotation

Start with the [quickstart](quickstart.md). Against each replica, make the following **unauthenticated** requests (example for a local listener):

```sh
curl -si http://localhost:5201/health/live
curl -si http://localhost:5201/health/ready
curl -si http://localhost:5201/metadata
```

| Observation | Interpretation and next check |
| --- | --- |
| Live 200, ready 200, metadata 503 | Possible on the stock, unconfigured host: liveness selects no checks; readiness selects only checks tagged `ready`. With no `Federation:Host`, the search and signing checks are not registered. Confirm effective [binding](binding.md) before declaring this deployment usable. [Program][program] · [registration][routes] · [health tests][health] |
| Live 200, ready 503, metadata 503 | Check `search-capabilities`: a configured host needs a usable published view. With no view a request can fail before either token validation or shard I/O. Check generation publication and current evidence, not just whether a shard URL responds. [Cold-host test][cold] · [publication tests][publication] |
| Live 200, ready 503, metadata 200 | The metadata route can use the view without fresh caller signing configuration. Check `caller-signing`, discovery and key freshness; also inspect other `ready`-tagged checks registered by your host. Do not disable authentication to turn this green. [Metadata][metadata] · [signing readiness tests][signing] |
| All three 200 | Proceed to a synthetic search on an admitted type, with a least-privilege caller and known test data. Run it through the ingress as well as directly. The probes bypass authentication, and metadata does not run the caller's search. [Program][program] · [metadata][metadata] · [authenticated search tests][search-tests] |

Use liveness for process restarts and readiness for traffic selection. Probe per replica so one cold instance does not hide behind a warm one. The HTTP health body is an aggregate (`Healthy`/`Unhealthy` in the cold-host test), not a per-shard diagnostic report. To distinguish failing checks in a custom host, inspect the named entries of `HealthCheckService` internally, as the [cold-host test][cold] does; do not expose identities or backend details on the public probe.

## Know what you can collect

| Signal in this checkout | How to use it |
| --- | --- |
| `ActivitySource` **and** `Meter` named `Ignixa.Federation` | Register `AddSource("Ignixa.Federation")` with your tracer provider and `AddMeter("Ignixa.Federation")` with your meter provider. Supply sampling, a reader/exporter and a destination in your composition; the stock [startup][program] and [search registration][routes] do not configure a collector or expose a metrics endpoint. [Source definitions][telemetry] |
| Client activity `shard {stage}` | The shard-call helper sets `federation.shard.alias` and `federation.stage` (e.g. `alpha`, `search`). Have your transport start and dispose the activity around each actual attempt. A subscribed listener can measure duration; the helper alone is not proof that a request reached a backend. It returns no activity without an interested listener. [Helper][activity] · [listener test][activity-test] |
| Observable counter `federation.request.deadline_callback_faults` | A process-wide count of deadline timer cancellations whose callbacks threw an aggregate exception. A positive **increase** is a reason to inspect cancellation handling. It is **not** a count of slow searches, 504 responses or all exhausted budgets. `BackendCalls` and `ResponseBytes` on a request budget are request-local accounting, not exported histograms. [Counter, timer and accounting][budget] |

The [design, §14][spec] calls for per-shard latency, calls per request, truncations, strictness failures, breaker state, cache hits/misses and credential health. Treat these as instrumentation requirements for your binding, **not** default dashboards or promised instruments. In particular, an absent cache metric does not diagnose failed public paging: the [HTTP continuation path][search] issues inline tokens.

## Acceptance test for your instrumentation

1. Run a synthetic first page with a known match on a known alias. At the ingress, count the route template, status and elapsed time. In the binding, compare the attempted calls and consumed bytes with the request budget; include retries. Check that an exported child span has the expected alias and stage. If it is missing, check the transport hook, then the source name, sampling and exporter. [Activity contract][activity] · [budget contract][budget]
2. In staging, withdraw the view, then restore it. Expect live to stay green and new capability-dependent requests to fail without new shard work while cold. Do not demand that an already-running request abandon its captured view. Alert on sustained loss of ready replicas, not on an isolated 503 from a single search. [Cold-host test][cold] · [overlap and recovery tests][publication]
3. Separate failures by HTTP status **and FHIR issue code**. For example, trend 400 `too-costly` separately from planner refusals; compare 502 shard failures with 504 timeouts rather than treating both as missed probes. Inspect warning outcome entries separately from HTTP failures for reverse includes. These are suggested aggregations of the [wire behavior][search], not built-in counters. See [Errors](errors.md) and [Limits](limits.md).
4. Inspect a sample of exported telemetry before enabling production traffic. Allowlist route templates, status, bounded stage names and configured aliases. Exclude raw paths with resource IDs, query strings, POST bodies, `ct`, tokens, claims and backend payloads. Apply the same redaction at the ingress, exporter and backend. Do not turn a caller pseudonym into a metric label. [Caller-correlation handling][privacy] · [host JWT log filter][routes] · [Security](security.md)

[program]: ../../src/Ignixa.Federation.Api/Program.cs
[routes]: ../../src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[metadata]: ../../src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs
[health]: ../../test/Ignixa.Federation.Tests.Integration/HealthEndpointTests.cs
[cold]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs
[publication]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchCapabilityPublicationTests.cs
[signing]: ../../test/Ignixa.Federation.Tests.Integration/Search/SigningReadinessTests.cs
[search-tests]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchTests.cs
[telemetry]: ../../src/Ignixa.Federation.Core/Diagnostics/FederationTelemetry.cs
[activity]: ../../src/Ignixa.Federation.Shards/Diagnostics/ShardActivity.cs
[activity-test]: ../../test/Ignixa.Federation.Shards.UnitTests/Diagnostics/ShardActivityTests.cs
[budget]: ../../src/Ignixa.Federation.Api/Budgets/RequestBudget.cs
[spec]: ../superpowers/specs/2026-11-19-fhir-federation-service-design.md
[search]: ../../src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[privacy]: ../guides/CallerCorrelation.md
