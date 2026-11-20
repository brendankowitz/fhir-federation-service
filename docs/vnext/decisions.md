# vNext decisions

**Status:** Proposed. All records are pending maintainer review.

## Purpose and scope

Record proposed defaults and where each proposal is implemented. PR names describe future slices, not existing pull requests. Nothing here changes the [v1 owner decisions](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#18-owner-decisions-resolved-2026-11-19) or enables a feature in a v1 deployment. Review disagreements and failed backend certification should lead to an updated proposal or a disabled operation, not a silent fallback.

## V1

**Proposed.** How do writes enter the federation? Default: off; enable qualified operations with independent write roles and credentials. Rationale: the first release can validate an exact shard and leave v1 reads alone. Reject: default-on writes, caller-token forwarding, and unqualified instance writes. v1 impact: none; D1, D2, and D4 hold. Implementing PR: **Write API and credentials**. See [HTTP contract](write-placement.md#http-contract).

## V2

**Proposed.** What decides ownership? Default: a sticky, versioned directory with a validated placement key. Use certified patient co-location or hashing to choose a new assignment, not to move one. Rationale: the directory models the fact that one owner persists across topology changes. Reject: `hash % shardCount`, caller-asserted tenant identity, and fan-out guessing for existing IDs. v1 impact: no query pruning or identity change. Implementing PR: **Placement directory and router**. See [placement and routing](write-placement.md#placement-and-routing).

## V3

**Proposed.** How much atomicity is supported? Default: certified single-shard transactions only; batch entries are independent; conditional interactions require strict and atomic single-placement proof. Rationale: a successful FHIR transaction promises atomicity, including under retries and races. Reject: saga compensation as transaction semantics, two-phase commit on generic REST, and federation-side search-then-mutate. v1 impact: none; compatible with D7 and v1 section 13. Implementing PR: **Single-shard interactions and routed batches**. See [batch and transaction](write-placement.md#batch-and-transaction).

## V4

**Proposed.** What may retry a mutation? Default: durable intent and receipts scoped to caller, request, and placement epoch. Unknown output remains unknown until certified evidence settles it. Rationale: retries must not create data twice or dispatch after a move to a new owner. Reject: blind retry, stream cache as write ledger, and treating lease expiry as proof of non-commit. v1 impact: D5 still applies to reads; write retries never use the v1 search snapshot store. Implementing PR: **Mutation ledger and reconciliation**. See [retries and consistency](write-placement.md#retries-and-consistency).

## V5

**Proposed.** What constitutes a successful export? Default: a complete shard-tagged manifest from pinned public membership with access controlled by direct storage grants. Rationale: relative references require provenance, and large downloads should not use the API's data path. Reject: best-effort partial success, API byte proxy, and signed bearer URLs. v1 impact: no partial search and no shape changes; retain D2 and D4. Implementing PR: **Federated export coordinator**. See [federated export](import-export.md#federated-export).

## V6

**Proposed.** How does import avoid overwrites and replay? Default: immutable, bounded create-only chunks with exact placement, create proof, and durable receipts. Start with fresh or exclusively fenced targets. Rationale: native upserts and unknown submissions make retry unsafe. Reject: cross-shard transaction fiction, implicit ID reassignment, and upsert fallback. v1 impact: none; D1 identity and relative-reference rules still apply. Implementing PR: **Federated import planner and worker**. See [federated import](import-export.md#federated-import).

## V7

**Proposed.** How are new and copied shards made visible? Default: append empty shards; use detached membership for staging and certify same-URL replacement first. Rationale: membership excludes copies before joins, counts, and reads. Reject: resource tags, post-merge filtering, and uncertified live-target migration. v1 impact: compatible with D3, D4, and D6; no automated migration in v1. Implementing PRs: **Versioned topology control**, then **Offline-backed shard replacement**. See [operations and admission](shard-rebalancing.md#operations-and-admission).

## V8

**Proposed.** What expires cursors after topology changes? Default: keep v1's content hash inputs, add a monotonic generation, backing epochs, and stable ordinals; valid vNext continuations on a different generation return `410`. Rationale: identical `BaseUrl` does not prove identical backend cursor semantics. Reject: moving ordinal positions, pairwise stream-only invalidation in topology-control mode, and extending leases indefinitely. v1 impact: v1 section 4.2 positions and section 7.4 selective invalidation remain unchanged when topology control is off. When enabled, explicit ordinals and whole-generation invalidation are intentional changes. D5's live, retryable read semantics remain. Implementing PR: **Versioned topology control**. See [registry and token contract](shard-rebalancing.md#registry-and-token-contract).

## V9

**Proposed.** What makes cutover reversible? Default: enforce backend write quiescence, drain affected jobs and readers, publish ownership once, verify read-only, then open writes. Rationale: a job lease does not fence an accepted backend request. Reject: app-only fencing, dual writes, and rollback to a stale source after destination writes. v1 impact: off by default. Implementing PR: **Move orchestration and fencing**. See [move state machine](shard-rebalancing.md#move-state-machine).

## V10

**Proposed.** What happens when a real URL changes? Default: block moves that cannot prove closure, suppress only proven private replicas, and retain authorized nonredirecting `410` tombstones. Rationale: D1 distinguishes resources on different shards and D4 does not permit rewriting response content. Reject: aliasing a new body to an old `fullUrl`, HTTP forwarding, and deduplication by `type/id` or timestamp. v1 impact: no change; disabled deployments maintain their existing read behavior, and enabled moves preserve v1 merge keys. Implementing PR: **Identity-changing split, merge, move, and drain**. See [identity, duplicates, and references](shard-rebalancing.md#identity-duplicates-and-references).

## Design principles applied

| Principle | Proposal that applies it |
|---|---|
| Foundational thinking | [V2](#v2) and [V8](#v8) define identity, placement, and immutable snapshots before routing. |
| Make operations idempotent | [V4](#v4), [V6](#v6), and [V9](#v9) reconcile mutations from persisted evidence before retry. |
| Separate before serializing shared state | [V5](#v5) and [V9](#v9) separate job checkpoints and serialize only ownership publication. |
| Model the domain | [V9](#v9) uses explicit transitions and ownership states instead of independent flags. |
| Boundary discipline | [V1](#v1), [V3](#v3), and [V5](#v5) validate grants, request shape, and origins before dispatch. |
| Minimize reader load | [V7](#v7) and [V10](#v10) give public reads one authoritative copy and one identity rule. |

## Release checks

| Case | Required evidence |
|---|---|
| Features disabled | Replay the v1 search, authorization, paging, and proxy-read corpus without acquiring write credentials. |
| Mutation times out after dispatch | Retry the same key on two instances, once after a grant change. No second request reaches a backend without certified proof. |
| Export fails or authorization changes | One failed child cannot publish any download grant. Revoking the grant for a completed job stops future downloads within the documented delay. |
| Import worker loses its receipt | A replacement worker reconciles the original shard job and does not resubmit unknown chunks. |
| Two moves overlap | Exactly one owns the reservation. After cutover, a stale token fails with `410` before any backend call. |
| The source and destination both hold a replica | Search, counts, includes, chains, and export each see only one authoritative copy. Two unrelated same-ID resources remain distinct. |

If certification leaves a case unproven, keep the affected capability disabled and record the missing proof in the implementing PR.
