# Federation differential-oracle fixtures

Static FHIR R4 seeds and hand-authored answers for the future .NET differential harness (design §4.1, §7.5, §7.6, and §15 item 2). The offline check needs no server, network, timestamps, or result order.

```
python -B test/fixtures/federation-oracle/check_fixtures.py   # exit 0 = OK, 1 = failure
```

## Files

- `manifest.json`: shards, file map, and the thirteen cases with their expected answers.
- `seeds/unique-id/oracle.json`: all five resources, relative references. Load only into the oracle server.
- `seeds/unique-id/{colocated,cross-shard}/{east,west}.json`: two placements of the oracle. Cross-shard moves only `Observation/obs-orchid-2` to `west`, changing only its subject to `https://fhir-east.example.org/Patient/p-orchid`.
- `seeds/collision/{east,west}.json`: separate corpus where `Patient/dup` and `Observation/obs-dup` exist on both shards (different given names, `Eli` east and `Gus` west). `west` also has `Observation/obs-dup-xref`, whose absolute subject is `https://fhir-east.example.org/Patient/dup`.

## Reference resolution (collision corpus)

A relative reference resolves on the shard that stores the referencing resource; an absolute reference resolves on the shard owning that base. Identity is the owning `fullUrl` (`<shard base>Type/id`). So `east` `obs-dup` and `west` `obs-dup` point at their own shard's `Patient/dup`, and `west` `obs-dup-xref` points at the east one. The checker re-derives every collision answer with one small source-aware resolver over a `(shard, Type/id)` index; the checked-in answers were derived by hand.

## Cases

Answers are oracle identities `Type/id` in the unique-id corpus and owning `fullUrl`s in the collision corpus.

| Case | Expectation |
|---|---|
| `chain.colocated.{passthrough,distributed}` | `Observation?subject:Patient.name=Orchid` equals the oracle: both Orchid Observations. |
| `chain.cross-shard.distributed` | Same answer as the oracle. |
| `chain.cross-shard.passthrough-control` | Only `obs-orchid-1` (surviving witness); `missedVsOracle` is `obs-orchid-2`. |
| `collision.tagged-union` | `Patient?name=Orchid&_count=1` returns two entries over two pages. `expectedFullUrls` lists both owning `fullUrl`s. |
| `collision.chain.distributed.east` | `Observation?subject:Patient.given=Eli`: east `obs-dup` and west `obs-dup-xref` (not west `obs-dup`). |
| `collision.chain.distributed.west` | `Observation?subject:Patient.given=Gus`: west `obs-dup` only (neither east `obs-dup` nor `obs-dup-xref`). |
| `collision.include.paged` | `Observation?_include=Observation:subject:Patient&_count=1`; relations are keyed by the owning Observation `fullUrl`. East `Patient/dup` is the target of two matches. |
| `collision.include.unpaged` | Same query without `_count`; witnesses are all three Observations, whose includes are both `Patient/dup` entries (two fullUrls, one Type/id, one target referenced twice). |
| `collision.revinclude.paged` | `Patient?name=Orchid&_revinclude=Observation:subject:Patient&_count=1`: east `dup` gets east `obs-dup` and west `obs-dup-xref`; west `dup` gets west `obs-dup`. |
| `include.forward` | `_include=Observation:subject:Patient&_count=1`: two matches share `p-orchid`; `p-cedar` differs. |
| `include.forward.unpaged` | Same query without `_count`; witnesses `obs-orchid-1` and `obs-orchid-2` share one included `Patient/p-orchid`. |
| `include.reverse` | `_revinclude=Observation:subject:Patient&_count=1`: `p-orchid` has two includes, above `_count`. |

## Harness contract

1. **Load.** For each placement or corpus, create fresh isolated shards. PUT with ids preserved: all Patients on both shards first, then all Observations (cross-shard references point to a Patient on the other shard). Load the oracle into its own fresh server, never into a shard. Replace the reserved `https://fhir-{east,west}.example.org/` bases with the real shard bases in seed references and throughout the expected answers, including `fullUrl` keys. Register those bases in the federation.
2. **Run each query on both** the federation and the unique-ID oracle. The oracle is not used for `collision.*` cases.
3. **Validate before projecting.** Check federation `fullUrl`s against the owning shard base. Check oracle `fullUrl`s against the oracle server's base, not a shard's. Project to `Type/id` only for unique-id cases.
4. **Chains.** Run each chain case in its stated mode and placement (Passthrough: `ChainResolution.Passthrough = ["Observation.subject:Patient"]`; Distributed: empty list). Compare as multisets, never by order. The control must still miss `obs-orchid-2`; if it returns it, the control has failed.
5. **Pages.** Follow each system's `next` links independently to the end. When the case has `count`, use that value on both systems and enforce it as a maximum for matches on each page. Compare concatenated match entries (`search.mode=match`) to the expected matches as a multiset. For the Passthrough control, the oracle must return `expectedMatches` plus `missedVsOracle`. Across pages, reject repeated match identities. Key collision matches by owning `fullUrl`, not `Type/id`.
6. **Includes.** Derive expected includes from each emitted page: the union of `includeRelations[m]` over that page's matches, minus resources that are matches on that page. Compare to that page's include entries. A shared target must appear on every page that needs it. Reject a duplicate include entry within a page. Includes do not consume `_count`. For collision cases, "the same entry" means the same `fullUrl`: two `Patient/dup` includes with different `fullUrl`s are both required, and a single one is a failure.
7. **Unpaged cases need co-emitted witnesses.** A case with `coEmitted` has no `count`. Request enough page capacity for every match (send no `_count` and configure the harness page size above the match total). The page that carries any `coEmitted` witness must carry all of them; otherwise the run is non-discriminating and fails (it is not a pass). On that page the include entries must equal the item 6 union (which contains `witnessIncludes`), with no duplicates. This is what exposes `Type/id` include deduplication across shards.
8. **Collision bodies.** In every collision case, check every emitted `Patient/dup`, whether a match or an include. Look up its returned `fullUrl` in `corpora.collision.expectedGiven` and require `name[0].given` to match. An unlisted URL fails. The east URL must carry `Eli`; the west URL must carry `Gus`. Do not require whole-body equality with the seed because the server assigns metadata.

## Name matching used by the checker

`name_matches` and `given_matches` apply FHIR string search semantics to the data present: case-insensitive prefix over `family` and `given` (`name`) or `given` only. Patient names may only carry `family` and `given`, so no other HumanName part can match. A seed such as `given: ["oRChidia"]` therefore matches `name=Orchid`, and answers that assumed an exact family match go stale.

## Backend hazards exercised

Microsoft FHIR Server treats a relative `Type/id` search value as `ReferenceKind.InternalOrExternal`: it can match the type and ID under any stored base. A west-shard search for `subject=Patient/dup` can therefore return west `obs-dup-xref` in `collision.chain.distributed.west` and for west `Patient/dup` in `collision.revinclude.paged`. That Observation refers only to the east Patient. The manifest keeps the spec-correct answers so the harness can detect the overmatch.

## Scope

The checker verifies shapes, ids, reference ownership, name matching, oracle equivalence, the single relocation, and the answers to the named queries. It is not a FHIR validator or search engine and does not test the federation or any backend. Not covered: `_has`, sorting, versioned references, truncated includes, or mirrored placement. Random placement belongs in the harness generator. These cases do not exercise a resource that is both a match and an include on the same page because the types are disjoint.
