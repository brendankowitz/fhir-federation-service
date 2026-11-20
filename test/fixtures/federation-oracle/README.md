# Federation differential-oracle fixtures

Static FHIR R4 seeds and hand-authored answers for the future .NET differential harness (design §4.1, §7.5, §7.6, §15.2). Plain JSON: no server, network, timestamps, or reliance on result order.

```
python -B test/fixtures/federation-oracle/check_fixtures.py   # exit 0 = OK, 1 = failure
```

## Files

- `manifest.json`: shards, file map, and the seven cases with their expected answers.
- `seeds/unique-id/oracle.json`: all five resources, relative references. Load only into the oracle server.
- `seeds/unique-id/{colocated,cross-shard}/{east,west}.json`: two placements of the oracle. Cross-shard moves only `Observation/obs-orchid-2` to `west`, changing only its subject to `https://fhir-east.example.org/Patient/p-orchid`.
- `seeds/collision/{east,west}.json`: separate corpus where `Patient/dup` exists on both shards.

## Cases (answers are oracle identities `Type/id`)

| Case | Expectation |
|---|---|
| `chain.colocated.{passthrough,distributed}` | `Observation?subject:Patient.name=Orchid` equals the oracle: both Orchid Observations. |
| `chain.cross-shard.distributed` | Same answer as the oracle. |
| `chain.cross-shard.passthrough-control` | Only `obs-orchid-1` (surviving witness); `missedVsOracle` is `obs-orchid-2`. |
| `collision.tagged-union` | `Patient?name=Orchid` returns two entries; `expectedFullUrls` lists both owning `fullUrl`s. |
| `include.forward` | `_include=Observation:subject:Patient&_count=1`: two matches share `p-orchid`; `p-cedar` differs. |
| `include.reverse` | `_revinclude=Observation:subject:Patient&_count=1`: `p-orchid` has two includes, above `_count`. |

## Harness contract

1. **Load.** For each placement or corpus, create fresh isolated shards. PUT with ids preserved: all Patients on both shards first, then all Observations (the cross-shard Observation references a Patient on the other shard). Load the oracle into its own fresh server, never into a shard. Before loading, replace the reserved `https://fhir-{east,west}.example.org/` bases with the real shard bases, in references and expected `fullUrl`s, and register those bases in the federation.
2. **Run each query on both** the federation and the unique-ID oracle. The oracle is not used for `collision.tagged-union`.
3. **Validate before projecting.** Check that each returned `fullUrl` belongs to its owning shard base, then project it to `Type/id`.
4. **Chains.** Run each `chain.*` case in its stated mode and placement (Passthrough: `ChainResolution.Passthrough = ["Observation.subject:Patient"]`; Distributed: empty list). Compare as multisets, never by order. The control must still miss `obs-orchid-2`; if it returns it, the control has failed.
5. **Pages.** Follow each system's `next` links independently to the end. When the query sets `_count`, use the same value on both systems and enforce it as a maximum for matches on each page. Compare concatenated match entries (`search.mode=match`) to `expectedMatches` as a multiset. For the Passthrough control, assert that the oracle instead returns `expectedMatches` plus `missedVsOracle`. A match repeated across pages fails.
6. **Includes.** Derive expected includes from each emitted page: the union of `includeRelations[m]` over that page's matches, minus resources that are matches on that page. Compare to that page's include entries. A shared target must appear on every page that needs it. Reject a duplicate include entry within a page. Includes do not consume `_count`.
7. **Collisions.** Compare the `fullUrl` multiset to `expectedFullUrls`; never map to `(type, id)`.

## Scope

The checker verifies shapes, ids, reference ownership, oracle equivalence, the single relocation, and that each answer matches the seeds for its fixed query. It is not a FHIR validator or search engine and does not test the federation or any backend. Not covered: `_has`, sorting, versioned references, truncated includes.
