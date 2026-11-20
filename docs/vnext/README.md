# Ignixa Federation vNext

**Status:** Proposed. Pending maintainer review. No runtime features ship with this spec.

## Purpose and scope

Ignixa Federation v1 fans out read-only FHIR R4 searches. vNext proposes three additions: place writes on one shard, move data with asynchronous bulk jobs, and change ownership without exposing staged data. A placement key groups data within a single federation. It is not a tenant isolation boundary.

These proposals do not add SMART authorization, a global resource namespace, cross-shard transaction atomicity, or a federation-wide point-in-time snapshot. With vNext disabled, the [v1 owner decisions](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#18-owner-decisions-resolved-2026-11-19) and the existing read-only API still apply.

## Reading order

| Document | Status | What it defines |
|---|---|---|
| [Write placement](write-placement.md) | Proposed | Router, credentials, FHIR interactions, and retries. |
| [Import and export](import-export.md) | Proposed | Asynchronous job contracts, data routing, and storage access. |
| [Shard rebalancing](shard-rebalancing.md) | Proposed | Registry generations, migration gates, and recovery. |
| [Decisions](decisions.md) | Proposed | Defaults, rejected alternatives, and implementation PR boundaries. |

## Dependencies on v1

Each row builds on the exit criteria in the [v1 phase plan](../superpowers/specs/2026-11-19-fhir-federation-service-design.md#16-phases). These are dependencies, not changes to v1 phases or promises about release dates.

| v1 phase | What vNext reuses or verifies |
|---|---|
| 0 | Backend profiles, origin checks, and reference classification. New write, bulk, and mover capabilities require independent certification. |
| 1 | Role checks, qualified reads, and continuation storage. Compare the disabled deployment with v1 before enabling any new path. |
| 2 | Targeted includes and revincludes. A staged copy must remain invisible to both. |
| 3 | Distributed chains, per-path passthrough assertions, and request budgets. Reject writes and moves that invalidate a passthrough assertion. |
| 4 | Sorted merge and exact counts. Compare both before and after a topology change. |
| 5 | Second backend, audit, and load tests. Certify Microsoft FHIR Server first and Ignixa second. Keep the 16-shard soft limit and the 16- and 50-shard load targets. |

Implement writes first, then export, then import. Add topology control and empty-shard addition before shipping certified data movement. See [V1](decisions.md#v1), [V5](decisions.md#v5), and [V7](decisions.md#v7).

## Failure boundaries

Missing capability proof disables the affected operation. An unknown write result stays pinned to its original target. A failed export does not publish an incomplete manifest as success. A stale vNext continuation returns `410`, not a page stitched across generations. See [V4](decisions.md#v4), [V5](decisions.md#v5), and [V8](decisions.md#v8).
