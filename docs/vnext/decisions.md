# vNext decisions

**Status:** Proposed. All records are pending maintainer review.

## Purpose and scope

Record proposed defaults and where each proposal is implemented. PR names describe future slices, not existing pull requests. Nothing here changes the [v1 owner decisions](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#18-owner-decisions-resolved-2026-11-19) or enables a feature in a v1 deployment. Review disagreements and failed backend certification should lead to an updated proposal or a disabled operation, not a silent fallback.

## V1

**Proposed.** How do writes enter the federation?

- **Default:** Keep writes off unless explicitly enabled. Expose qualified operations with independent write roles and credentials.
- **Rationale:** Qualified routes let the federation validate an exact shard without changing v1 reads.
- **Alternatives rejected:** Default-on writes, caller-token forwarding, and unqualified instance writes.
- **Consequences:** Operators provision and rotate separate credentials. Clients must know the shard alias.
- **v1 impact:** None. D1, D2, and D4 hold.
- **Implementing PR:** **Write API and credentials**. See [HTTP contract](write-placement.md#http-contract).

## V2

**Proposed.** What decides ownership?

- **Default:** Use a sticky, versioned directory keyed by a validated placement key. Use certified patient co-location or hashing to choose new assignments, not to move existing ones.
- **Rationale:** An assignment persists across topology changes.
- **Alternatives rejected:** `hash % shardCount`, caller-asserted tenant identity, and fan-out guessing for existing IDs.
- **Consequences:** The directory is a stateful readiness dependency for routed writes. Operators must census existing data before enabling those writes.
- **v1 impact:** No query pruning or identity change.
- **Implementing PR:** **Placement directory and router**. See [placement and routing](write-placement.md#placement-and-routing).

## V3

**Proposed.** How much atomicity is supported?

- **Default:** Allow certified single-shard transactions. Batch entries are independent. Conditional interactions require proof of strict predicates and an atomic action within one placement.
- **Rationale:** A successful FHIR transaction promises atomicity, including under retries and races.
- **Alternatives rejected:** Saga compensation as transaction semantics, two-phase commit on generic REST, and federation-side search-then-mutate.
- **Consequences:** Clients must keep atomic work on one shard and handle partial batch results. Backends without certified conditional semantics cannot expose those operations.
- **v1 impact:** None. D7 and v1 section 13 still apply.
- **Implementing PR:** **Single-shard interactions and routed batches**. See [batch and transaction](write-placement.md#batch-and-transaction).

## V4

**Proposed.** What may retry a mutation?

- **Default:** Key durable intents and receipts by federation, stable caller identity, interaction kind, and idempotency key. Store the request fingerprint and original placement epoch as record fields. A new epoch never makes the same key new. Keep unknown outcomes unresolved until certified evidence settles them.
- **Rationale:** Retries must not create data twice or dispatch to a new owner after a move.
- **Alternatives rejected:** Blind retry, a stream cache as a write ledger, and lease expiry as proof that a write did not commit.
- **Consequences:** Clients must supply `Idempotency-Key` and keep it across retries. Clients without that support cannot write. Operators must keep the durable ledger available and reconcile unknown writes before moving affected data.
- **v1 impact:** D5 still applies to reads. Write retries do not use the v1 search snapshot store.
- **Implementing PR:** **Mutation ledger and reconciliation**. See [retries and consistency](write-placement.md#retries-and-consistency).

## V5

**Proposed.** What constitutes a successful export?

- **Default:** Publish a complete shard-tagged manifest from pinned public membership. Use direct grants for file downloads.
- **Rationale:** Relative references require provenance. Clients can download large files without sending the bytes through the federation API.
- **Alternatives rejected:** Best-effort partial success, API byte proxy, and signed bearer URLs.
- **Consequences:** A failed shard can fail the job. Operators administer storage grants and expiry. Clients must request a token for the storage audience, which stock Bulk Data clients may not support.
- **v1 impact:** No partial search or response-shape changes. D2 and D4 still apply.
- **Implementing PR:** **Federated export coordinator**. See [federated export](import-export.md#federated-export).

## V6

**Proposed.** How does import avoid overwrites and replay?

- **Default:** Use immutable, bounded create-only chunks with exact placement, create proof, and durable receipts. Start with fresh or exclusively fenced targets.
- **Rationale:** Native upserts and unknown submissions make retries unsafe.
- **Alternatives rejected:** Cross-shard atomicity, implicit ID reassignment, and fallback to upsert.
- **Consequences:** Operators provision staging storage and reserve a write window when a target needs a fence. Clients supply explicit, non-colliding IDs and handle committed partial progress.
- **v1 impact:** None. D1 identity and relative-reference rules still apply.
- **Implementing PR:** **Federated import planner and worker**. See [federated import](import-export.md#federated-import).

## V7

**Proposed.** How are new and copied shards made visible?

- **Default:** Append empty shards. Stage copies outside public membership and certify same-URL replacement first.
- **Rationale:** Membership excludes copies before joins, counts, and reads.
- **Alternatives rejected:** Resource tags, post-merge filtering, and uncertified live-target migration.
- **Consequences:** Replacements need extra capacity and controlled ingress. Clients can see a pause at cutover even when the real shard URL stays the same.
- **v1 impact:** No automated migration. D3, D4, and D6 still apply.
- **Implementing PRs:** **Versioned topology control**, then **Offline-backed shard replacement**. See [operations and admission](shard-rebalancing.md#operations-and-admission).

## V8

**Proposed.** What expires cursors after topology changes?

- **Default:** Keep v1's content hash inputs and add a monotonic generation, backing epochs, and stable ordinals. Return `410` for valid vNext continuations from a different generation.
- **Rationale:** The same `BaseUrl` does not prove that the backend can redeem a previous cursor.
- **Alternatives rejected:** Moving ordinal positions, stream-only invalidation in topology-control mode, and unbounded leases.
- **Consequences:** Operators must maintain the generation and lease store. Each topology change makes clients restart every open search, including those unaffected by the changed shard.
- **v1 impact:** Sections 4.2 and 7.4 remain unchanged when topology control is off. Explicit ordinals and whole-generation invalidation apply only when enabled. D5 still applies.
- **Implementing PR:** **Versioned topology control**. See [registry and token contract](shard-rebalancing.md#registry-and-token-contract).

## V9

**Proposed.** What makes cutover reversible?

- **Default:** Fence backend writes. Drain affected jobs and readers, publish ownership once, verify read-only, and then open writes.
- **Rationale:** A job lease does not fence an accepted backend request.
- **Alternatives rejected:** App-only fencing, dual writes, and rollback to a stale source after destination writes.
- **Consequences:** Operators need a maintenance window and proof that pending writes stopped. Clients must wait for writes to reopen. Unresolved receipts can delay cutover indefinitely.
- **v1 impact:** No change. Moves are off by default.
- **Implementing PR:** **Move orchestration and fencing**. See [move state machine](shard-rebalancing.md#move-state-machine).

## V10

**Proposed.** What happens when a real URL changes?

- **Default:** Block moves that cannot prove reference closure. Suppress only proven private replicas. Retain tombstones that return `410` to authorized callers without a redirect.
- **Rationale:** D1 distinguishes resources on different shards. D4 rules out rewriting response content.
- **Alternatives rejected:** Serving a destination body under the old `fullUrl`, HTTP forwarding, and deduplication by `type/id` or timestamp.
- **Consequences:** Operators must check references and retain tombstones. Clients with old URLs must find the resource again where their authorization permits.
- **v1 impact:** None when moves are disabled. Enabled moves preserve v1 merge keys.
- **Implementing PR:** **Identity-changing split, merge, move, and drain**. See [identity, duplicates, and references](shard-rebalancing.md#identity-duplicates-and-references).

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
