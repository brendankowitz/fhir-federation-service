# Write placement

**Status:** Proposed. Pending maintainer review.

## Purpose and scope

Route each accepted mutation to one authorized FHIR R4 shard. Writes are opt-in and off by default. Disabling writes does not change read routes, credentials, capabilities, authorization, or error mapping. Search paging retains its v1 contract unless the separate topology-control switch is on. Neither enabling writes nor choosing a placement strategy adds shard-less reads, tenant isolation, SMART scopes, or distributed transaction atomicity.

See v1 [identity](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#4-identity-and-references), [continuations](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#74-continuation-tokens), [backend profiles](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#92-backend-profiles), and [security](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#10-security).

## Placement and routing

The qualified route names a physical shard. The router validates that choice against the operation's placement and authorization. For managed partitions, the default is a sticky directory keyed by a validated partition key. An unknown existing resource never falls back to a newly assigned shard. Before routing any legacy data, establish and verify a placement census. Keep v1 read fan-out; placement is not evidence that a read may skip a shard.

| Strategy | Input and constraint |
|---|---|
| Tenant or partition key | Derive the key from authenticated configuration or a certified resource mapping. Never trust a body or header as proof of tenancy. A tenant key denotes a placement unit within one trust boundary. |
| Patient-compartment co-location | Use a certified R4 compartment mapping and resolve the patient in its shard context. Reject absent, conflicting, or multiple anchors unless an explicit validated policy can select one owner. Validate passthrough paths before commit. |
| ID hash | Hash the canonical type and case-sensitive ID into a fixed bucket map. Seed a new assignment, then persist it. Never rehash existing resources on shard-count changes. |
| Directory lookup | Look up an existing assignment in the snapshot by key or exact qualified identity. Never interpret a plain `type/id` as globally unique. A missing or ambiguous mapping fails closed. |

Proposal in `Ignixa.Federation.Core`:

```text
IShardRouter.Resolve(PlacementInput input, RoutingSnapshot snapshot) -> PlacementDecision
PlacementDecision = Target(shardId, generation, placementEpoch) | Reject(reason)
```

`PlacementInput` contains the validated interaction and scope, and any qualified identity, resource placement facts, or allocated ID. `RoutingSnapshot` contains the immutable registry generation, policy version, and the directory records used in this decision. The same inputs yield the same result. Resolve does not perform I/O or allocate IDs. An admission component reads the relevant records, claims a new key with compare-and-swap if needed, and re-resolves against the winning assignment. A new-key insert does not bump the topology generation. Reassignment requires the [move protocol](shard-rebalancing.md#move-state-machine); changing a placement key in an update returns `409`.

The distinction between separate read and write placement comes from Ignixa's partition strategy and `RequestPartition` model. Patient co-location comes from the Distributed Sharding investigation. Ignixa's isolated and distributed tenant modes and tenant-provider context inform the separation between placement and authorization; this proposal imports none of those internal types. See [V1](decisions.md#v1) and [V2](decisions.md#v2).

## HTTP contract

`[base]` is the federation base. The first implementation exposes qualified writes only. A later capability may expose routed create at `POST [base]/[type]` for a type with a configured, unambiguous placement rule. It still does not expose unqualified instance writes or reads.

| Proposed request | Behavior after enablement |
|---|---|
| `POST [base]/shards/{alias}/[type]` | Create on the selected owner. For native POST, the backend allocates the public ID. Where placement requires a preallocated ID, use a certified atomic create-with-known-ID backend operation; preserve public POST semantics. Otherwise do not enable that route and policy combination. |
| `PUT [base]/shards/{alias}/[type]/[id]` | Require a valid client-assigned ID and exact agreement with the body ID. Require `If-Match` on managed updates. Create at an unused ID only when the profile certifies atomic create-if-absent, the ID is not retired, and admission establishes the placement. |
| `DELETE [base]/shards/{alias}/[type]/[id]` | Delete the current resource on that shard. Require the current version precondition on managed records and map certified backend denial without retry. |
| Qualified type-level conditional interactions | `If-None-Exist` for POST, search-based PUT and DELETE. Defer until a profile proves strict predicates, one authorized placement, and atomic match plus action under concurrent mutations. |
| `POST [base]/shards/{alias}` | Single-shard `batch` or `transaction` where certified. A later opt-in `POST [base]` may accept routed cross-shard batches. It must reject a transaction routed to more than one shard before dispatch. |
| PATCH | Not advertised in the first release. |

For conditional create, zero matches create, one returns the existing result without modifying it, and multiple return `412`. For conditional update, zero matches can create only on a certified profile with a safe ID rule; one updates; multiple return `412`. For conditional delete, zero is a no-op, one deletes, and multiple return `412`; no multiplicity option. A predicate must prove exact placement and authorization before the backend evaluates it. Never use fan-out search followed by mutation or let `Prefer: handling=lenient` loosen admission. Return `400` for unsupported predicates in line with D7. See [V3](decisions.md#v3).

Conditional create can return the existing resource only if the caller also has the read grant. The initial mutation API returns minimal receipts. Representation and bundle read entries, if added later, require the same read authorization as v1. A conditional mutation also requires read authorization for every type used in its predicate.

On create, return `201`; on replace, `200`; on delete, `204`. Normalize `Location` and bundle `entry.response.location` against the certified shard origin to `{shardBaseUrl}/{type}/{id}` with `/_history/{version}` when supplied. A bundle `entry.fullUrl`, when present for a returned resource, is the versionless shard URL. Preserve response bodies and valid backend ETags. Federation-generated operation URLs point to the federation. A malformed or foreign backend location fails closed; if the request may have committed, record an unknown result rather than imply failure-before-dispatch.

## Batch and transaction

Preflight the whole bundle before any dispatch. Validate entry methods, URLs, roles, references, profiles, and limits. Resolve transaction-local `urn:uuid` references inside the same shard transaction; foreign origins and dependencies across independent batch entries are not supported. Group independent batch entries by shard, execute with bounded concurrency, and restore the original entry order in the `batch-response` Bundle. A batch has no atomicity, even when it targets one shard. A failed group cannot undo another group. Report uncertainty per affected entry, not overall success. Return a `transaction-response` Bundle only on a certified single-shard transaction success.

A transaction is atomic only inside one certified backend transaction. A cross-shard transaction returns `422` with `OperationOutcome` `not-supported`, before any mutation. A saga exposes intermediate state and compensation is not rollback. Two-phase commit requires participant protocols that REST does not provide and is excluded by v1 section 13. See [V3](decisions.md#v3).

## Retries and consistency

Require `Idempotency-Key` for every submitted write or bundle. Scope the durable record to federation, stable caller identity, interaction kind, and key. Store the effective grant, a keyed fingerprint of the canonical request, target, epoch, state, and sanitized receipt; do not store resource bodies in the ledger. A change of grant or requested shard does not make the key new. Claim the key before dispatch and journal intent before network I/O. Distributed cache entries and expiring worker leases are not commit evidence.

Recheck authorization before returning a stored receipt. Reuse of a key with changed input returns `409`. A completed duplicate returns its original status, stable outcome, and locations without invoking the backend; the guaranteed replay representation is minimal. Concurrent attempts see pending state. Record individual child receipts for a batch. Use separate mutation clients with automatic retries, hedging, and redirect following disabled.

Register each dispatch under the current placement epoch before sending to the backend. The move coordinator closes admission for the epoch before it counts pending dispatches. Dispatch, reconciliation, and cutover follow the same contract for individual writes, bundle entries, and import chunks. A bookkeeping lease never replaces the [backend fence](shard-rebalancing.md#move-state-machine).

On a timeout or crash after possible dispatch, preserve the original target and mark the result unknown. Accept certified transaction receipts or a backend idempotency lookup as proof. A PUT read-back is proof only when a certified precondition, exclusive writer, and operation attribution tie the observed version to the intent; equality of payload alone is not proof. A generic POST timeout without a receipt can stay unresolved. Absence of the resource or expiration of a lease does not prove that the backend cannot still commit. Never retry with a new key to evade uncertainty, replay to another shard, or let a mover discard outstanding unknowns. Keep unresolved keys blocked until reconciled and retain terminal deduplication records for at least the advertised retry window.

After a successful write, the qualified read can use the recorded shard. A read-your-writes guarantee is advertised only for a profile certified to provide it. New search requests see backend index freshness; an existing continuation is immutable routing and cursor state over live data, not a read snapshot. A write may change later pages or remove the seek key, which returns `410` per v1. A vNext topology-generation change also returns `410` under [V8](decisions.md#v8). See [V4](decisions.md#v4).

## Credentials and failure modes

Keep v1 read-only per-shard credentials unchanged. Use separate per-shard identities for mutations. Give each identity only the required operations and use a separate administrative identity for moves. Role policies authorize interaction, type, and placement before dispatch; an authority that cannot be enforced independently on the backend is not eligible. Reject SMART tokens as in v1. Use the same exact HTTPS origin and base-path policy for every backend hop; never forward caller tokens or follow redirects. Block other writers to managed resources through backend authorization, including direct writes at URLs published by D1. Audit caller, operation, placement, epoch, correlation ID, and outcome to a protected sink. Exclude payloads, credential material, and raw idempotency keys.

| Failure | Response or action |
|---|---|
| Invalid ID, body, key, or unsupported conditional parameter | `400`, `invalid`, `required`, or `not-supported` as applicable. |
| Caller authentication missing or grant insufficient | `401` or `403`. Do not reveal external placement or resource existence. |
| Route disabled or operation unsupported | `405` with `Allow` where applicable, or `501` for an unimplemented operation. Do not fetch mutation credentials. |
| Collision, placement mismatch, or settled key reused for different input | `409`, `conflict`. |
| Backend version precondition or conditional cardinality fails | `412`, `conflict` or `multiple-matches`. |
| Ownership fenced, dependency unavailable, or ledger unavailable before dispatch | `503`, `transient`; return `Retry-After` when known. |
| Post-dispatch deadline or uncertain transport | First response `504` or `502`, with a sanitized unknown-outcome diagnostic. Same-key attempts return `409` until reconciled. |
| Backend authentication or profile drift | `502`, `exception`; alert for the shard, never convert to caller `401` or `403`. |
| Retired qualified identity | Authorized federation callers receive `410`, never a redirect. |
