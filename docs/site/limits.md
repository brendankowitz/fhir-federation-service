---
title: Limits and budgets
description: Size a federated search, distinguish enforced limits from transport requirements, and decide what to change when a request runs out of budget.
sidebar_position: 11
---

# Limits and budgets

**Budget one incoming request, not one shard or one returned resource.** A search can spend its allowance discovering witnesses before it returns a single match. Cursor follows, retries and speculative fetches also cost calls. A GET for the next federation page is a new request; a backend `next` followed while filling the current page is not. [Request budget][budget] · [search endpoint][endpoint] · [page executor][page].

Start with the [search support matrix](status.md) to check that the host executes the requested shape. Use [configuration binding](binding.md) and the [configuration reference](configuration.md) to set the options below. The [design, §§7–8][design] describes the intended service; the implementation and tests linked here distinguish what this revision actually enforces.

## Choose limits for a deployment

These are the defaults under **`Federation:Limits`**. Every value must be positive, `DefaultPageSize ≤ MaxPageSize ≤ 100`, and `BackendCallsPerShard × configured shard count` must fit a signed 32-bit integer. These are configuration checks, distinct from a refusal of an individual search. [Limit options][limits] · [validator][validation] · [validation tests][validation-tests].

| Setting | Default | What to size for |
| --- | ---: | --- |
| `DefaultPageSize` | 20 matches | Used when `_count` is absent. `_count=0` requests a count, not a cheap empty page: an exact count needs support from the participating owners. [Planner][planner] · [count tests][counts]. |
| `MaxPageSize` | 100 matches | A larger positive `_count` is clamped, not refused. On an unsorted page the next initial shard request asks for the lesser of the remaining matches and that shard profile's `MaxCount`. Small backend pages can therefore require more calls. [Planner][planner] · [shard stream][stream] · [page executor][page]. |
| `MaxChainKeys` | 1,000 distinct keys | Bounds the keys resolved for a distributed chain clause, across its witnesses, not per shard. Exactly 1,000 fit; a proven 1,001st distinct physical key is too costly. A smaller primary `_count` does not narrow discovery. [Forward resolver][forward] · [reverse paging tests][reverse-tests] · [dispatcher][route]. |
| `MaxChainSourceRows` | 5,000 scanned rows | Additional limit per distributed reverse-chain (`_has`) clause. Repeated references may resolve to one key but the source rows still count. The row and key caps are per clause; the call allowance is shared across clauses. [Witness accounting][witnesses] · [reverse paging tests][reverse-tests]. |
| `MaxIncludesPerPage` | 1,000 includes | Separate from match slots. With a bound `_revinclude` resolver, exactly filling the cap is silent; a further eligible include yields a page with `search.mode=outcome`, code `incomplete`. A required fetch failing or exceeding budget is **not** truncation. Forward `_include` is not an executable HTTP search in this revision. [Include endpoint tests][includes] · [dispatcher][route]. |
| `BackendCallsPerShard` | 40 attempts | Multiplier for **one pool**: a busy owner can use more than 40. A granted send is never refunded; the first attempt over the pool must not be sent. [Request budget][budget] · [transport contract][transport]. |
| `MaxResponseBytes` | 67,108,864 bytes (64 MiB) | Sum of backend response bodies consumed, not the size of the outgoing Bundle, nor an allowance per shard. Exactly 64 MiB fits; the full chunk that crosses the limit is charged. [Request budget][budget] · [budget tests][budget-tests]. |
| `MaxParallelBackendCalls` | 16 | Per request, not a process-wide throttle or a promise of 16 concurrent calls. Unsorted paging fetches the first active stream alone; one slot stays available for it if later streams are fetched speculatively. Setting 1 disables that speculation. Count and reverse-witness fan-out also take this setting. [Page executor][page] · [dispatcher][route]. |
| `ShardCountWarningThreshold` | 16 shards | Soft planning threshold, **not** a hard cap. The option declares a startup warning, but the validator only checks positivity; there is no warning emitter wired in this revision. Set your own registry-size alert. The design's 16- and 50-shard load targets are not measured capacity. [Limit options][limits] · [validator][validation] · [host startup][startup] · [design, D6][design]. |

### Check who enforces time and cost

Under **`Federation:Timeouts`**, the defaults are `Request = 00:00:30`, `BackendCall = 00:00:10`, and `ChainSubquery = 00:00:15`. All must be positive; neither inner timeout may exceed `Request`. The three values are **not additive**. [Timeout options][timeouts] · [validator][validation].

- **Host:** The request clock covers authentication, reading the form, planning, shard work and materializing the result. The final checkpoint prevents a latched cost breach from becoming a successful response. Request expiry returns 504 `timeout`; caller cancellation propagates separately. The elapsed deadline is checked even if the timer callback has not fired. [Request budget][budget] · [dispatcher][route] · [HTTP failure tests][failures].
- **Executor:** A chain subquery deadline bounds witness discovery, not each witness page afresh. The Core forward resolver wraps its client with the backend-call deadline; the HTTP reverse resolver receives the chain deadline. The ordinary search route does **not** install a universal 10-second timer around every physical send. Verify per-call cancellation on the client your host supplies. [Forward resolver][forward] · [deadline wrapper][call-deadline] · [reverse deadline tests][reverse-deadline] · [dispatcher][route].
- **Transport:** The executor limits logical fetches. The composed search client must also charge the live budget **before every physical attempt**, including retries, and report each consumed response chunk. The qualified-read binding has the same obligation. Without those callbacks the host cannot infer physical cost from a result. Test a retried call and an oversized body on the actual binding; do not assume the design's GET retry and breaker policy is installed by setting a timeout. [Search transport contract][transport] · [read contract][read] · [HTTP failure tests][failures] · [design, §8][design].

## Work a request budget before changing it

The following are sizing examples, not throughput measurements. Keep the workload shape fixed when comparing a proposed change.

| Situation | Arithmetic and decision |
| --- | --- |
| A four-shard `_has` count is close to its call allowance. | `4 × 40 = 160` sends. Suppose witness discovery uses 152 sends, including cursor follows, plus 4 retry attempts: **4 remain**. One count on each of four surviving owners fits exactly; another retry would be send 161 → 400 `too-costly`, no total. Narrow the witness predicate or fix the retry cause before raising the multiplier. Do not reserve 40 for each owner. [Request budget][budget] · [reverse count dispatch][route] · [count executor][count-executor]. |
| A page of large Observations fails although few matches were returned. | If earlier responses consumed 60 MiB, **4 MiB remain**. Another 4 MiB fits; another byte does not. A 5 MiB response would bring consumption to 65 MiB, even if most entries never appear in the outgoing Bundle. Reduce `_count` if primary matches dominate; if included resources dominate, remove the include or narrow the search. Raising the call cap buys no bytes. [Request budget][budget] · [page executor][page] · [HTTP failure tests][failures]. |
| A reverse chain finds few Patients in many Observation rows. | Three shards scan 1,800 + 1,700 + 1,501 = **5,001 rows** for one clause. Even 120 distinct Patient keys cannot save it: rows exceed 5,000. Conversely, 2,000 rows yielding 1,001 distinct keys exceed the key cap. Restrict the Observation witness predicate; reducing the Patient page from 100 to 20 does not change either calculation. Even below both caps, the rewritten owner request must fit its selected transport before it is sent. [Witness accounting][witnesses] · [reverse paging tests][reverse-tests] · [reverse resolver][reverse]. |
| A Patient page needs reverse includes from every shard. | Two authorized include directives across four shards need **at least 2 × 4 = 8 first fetches** in the Core resolver. With seven left after primary paging, refuse the request rather than issue a partial page. Remove an unnecessary directive or reduce the primary work. With sufficient fetches, 20 matches plus 1,001 distinct eligible includes instead yields 20 matches, up to 1,000 includes and an `incomplete` outcome. Treat those includes as incomplete, not as a complete clinical result. [Reverse-include executor][rev-executor] · [include endpoint tests][includes]. |
| A new deployment has 32 shards. | A count needing one call per shard at parallelism 16 takes at least **two waves**. At 7 s per wave, allow about 14 s before other work; if 18 s of the 30 s request have already elapsed, only 12 s remain. More call allowance cannot avert that deadline. Ten such simultaneous requests could demand `10 × 16 = 160` in-flight calls. Measure shard latency and cap incoming concurrency before increasing per-request parallelism. [Count executor][count-executor] · [request budget][budget] · [timeout defaults][timeouts]. |

## Distinguish size refusals from exhausted work

- **Inbound query or form:** The dispatcher accepts at most **8,192 raw query characters** (excluding `?`) and, separately for POST, **32,768 body bytes**. Excess gives 414 or 413 `too-long`, respectively, including a streamed body with no declared length. For a long initial GET use a UTF-8 `application/x-www-form-urlencoded` POST if it fits; this does not increase the work budget or enable POST continuation redemption. [Dispatcher][route] · [HTTP failure tests][failures].
- **Outbound shard request:** A request can fit ingress yet expand into an owner-specific query that does not fit the shard. The fit check is method- and shard-specific, side-effect free, and distinct from `MaxResponseBytes`. Narrow the join instead of retrying an identical oversized rewrite. [Transport contract][transport] · [forward fit tests][fit] · [reverse resolver][reverse].
- **Outbound next link:** `Federation:Tokens` defaults to `MaxInlineTokenBytes = 2048` (plaintext envelope, not final URL characters), `SlidingLifetime = 00:15:00`, `AbsoluteLifetime = 01:00:00`. All must be positive, sliding ≤ absolute. The HTTP route issues inline only, despite the cache fallback described in the design. If the token cannot be issued or its GET cannot fit the raw-query and configured Kestrel request-line limits (including the public base path), it returns **503 with no page**, not 400 `too-costly`. Shorten the saved query and check the actual next GET through ingress before increasing the inline limit. For expiry, replay and replica configuration see [Paging and continuations](server-paging.md). [Token options][tokens] · [validator][validation] · [token protector][protector] · [dispatcher][route] · [next-link tests][failures] · [design, §7.4][design].

For a 400 `too-costly`, identify the stage and exhausted unit before retrying. For a 504, investigate elapsed time; for a 502 on a required shard, investigate that shard rather than increasing a budget. Neither a failed required stage nor an exhausted request returns a partial successful Bundle. [HTTP outcomes][outcomes] · [HTTP failure tests][failures]. See [HTTP errors](errors.md), [observability](observability.md) and [troubleshooting](troubleshooting.md) for the on-call workflow.

[design]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md
[limits]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Configuration/LimitOptions.cs
[timeouts]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Configuration/TimeoutOptions.cs
[tokens]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Configuration/TokenOptions.cs
[validation]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Configuration/FederationOptionsValidator.cs
[validation-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Core.UnitTests/Configuration/FederationOptionsValidatorTests.cs
[startup]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Program.cs
[planner]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Search/SearchPlanner.cs
[budget]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Budgets/RequestBudget.cs
[budget-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Budgets/RequestBudgetTests.cs
[transport]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/IShardClientFactory.cs
[read]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Reads/IShardReadBinding.cs
[endpoint]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs
[route]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs
[outcomes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/SearchOutcome.cs
[failures]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchFailureTests.cs
[stream]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/ShardStream.cs
[page]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/UnsortedPageExecutor.cs
[counts]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchCountTests.cs
[count-executor]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/Counts/FederatedCountExecutor.cs
[forward]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/Chains/ForwardChainResolver.cs
[call-deadline]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/Chains/DeadlineShardClient.cs
[fit]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Core.UnitTests/Execution/Chains/ForwardChainHostFitTests.cs
[reverse]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/ReverseChains/ReverseChainResolver.cs
[witnesses]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/ReverseChains/ReverseChainWitnesses.cs
[reverse-tests]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Core.UnitTests/Execution/ReverseChains/ReverseChainPagingTests.cs
[reverse-deadline]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Core.UnitTests/Execution/ReverseChains/ReverseChainWitnessDeadlineTests.cs
[includes]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchRevIncludeTests.cs
[rev-executor]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Core/Execution/References/RevIncludePageExecutor.cs
[protector]: https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Continuations/ContinuationProtector.cs
