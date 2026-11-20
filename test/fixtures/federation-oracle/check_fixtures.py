#!/usr/bin/env python3
"""Offline checks for the federation-oracle fixtures (stdlib only).

Verifies seed integrity and the named cases in manifest.json against the seeds.
Expected answers are re-derived with one source-aware reference resolver over a
(shard, "Type/id") index. It is not a FHIR validator or search engine.
Usage: check_fixtures.py [root]
"""
import json
import re
import sys
from collections import Counter
from pathlib import Path

REF = re.compile(r"(https://[^/]+/)?Patient/([A-Za-z0-9.-]{1,64})")
STATUS = {"registered", "preliminary", "final", "amended", "corrected",
          "cancelled", "entered-in-error", "unknown"}
KEYS = {"Patient": {"resourceType", "id", "active", "name"},
        "Observation": {"resourceType", "id", "status", "code", "subject"}}
HUMAN_NAME_KEYS = {"family", "given"}
CHAIN_QUERY = "Observation?subject:Patient.name=Orchid"
FWD = "Observation?_include=Observation:subject:Patient"
REV = "_revinclude=Observation:subject:Patient"
# Non-unique-chain cases: id -> (kind, corpus, query, _count or None for an unpaged case)
SPEC = {
    "include.forward": ("include-forward", "unique-id", FWD + "&_count=1", 1),
    "include.forward.unpaged": ("include-forward", "unique-id", FWD, None),
    "include.reverse": ("include-reverse", "unique-id", "Patient?" + REV + "&_count=1", 1),
    "collision.tagged-union": ("identity-union", "collision", "Patient?name=Orchid&_count=1", 1),
    "collision.chain.distributed.east": ("chain-forward", "collision", "Observation?subject:Patient.given=Eli", None),
    "collision.chain.distributed.west": ("chain-forward", "collision", "Observation?subject:Patient.given=Gus", None),
    "collision.include.paged": ("include-forward", "collision", FWD + "&_count=1", 1),
    "collision.include.unpaged": ("include-forward", "collision", FWD, None),
    "collision.revinclude.paged": ("include-reverse", "collision", "Patient?name=Orchid&" + REV + "&_count=1", 1),
}
CASES = set(SPEC) | {"chain.colocated.passthrough", "chain.colocated.distributed",
                     "chain.cross-shard.passthrough-control", "chain.cross-shard.distributed"}


def need(ok, msg):
    if not ok:
        raise AssertionError(msg)


def given_matches(patient, value):  # string search: case-insensitive prefix
    return any(g.casefold().startswith(value.casefold()) for g in patient["name"][0]["given"])


def name_matches(patient, value):  # `name` covers family and given (HumanName is restricted to those)
    return given_matches(patient, value) or patient["name"][0]["family"].casefold().startswith(value.casefold())


MATCH = {"name": name_matches, "given": given_matches}


def shape(r):
    t, i = r.get("resourceType"), r.get("id")
    need(t in KEYS and set(r) <= KEYS[t], f"unknown resource shape {t}/{i}")
    need(isinstance(i, str) and re.fullmatch(r"[A-Za-z0-9.-]{1,64}", i), f"invalid id {i!r}")
    if t == "Patient":
        need(isinstance(r.get("active"), bool), f"Patient/{i}: active must be boolean")
        names = r.get("name")
        need(isinstance(names, list) and len(names) == 1, f"Patient/{i}: expected one name")
        name = names[0]
        need(set(name) <= HUMAN_NAME_KEYS, f"Patient/{i}: unexpected HumanName keys {sorted(set(name) - HUMAN_NAME_KEYS)}")
        need(isinstance(name.get("family"), str) and name["family"], f"Patient/{i}: name.family required")
        given = name.get("given")
        need(isinstance(given, list) and given and all(isinstance(n, str) and n for n in given),
             f"Patient/{i}: expected nonempty name.given strings")
    else:
        need(r.get("status") in STATUS, f"Observation/{i}: invalid status {r.get('status')!r}")
        codings = r.get("code", {}).get("coding") or []
        need(isinstance(codings, list) and codings
             and all(isinstance(c.get("code"), str) and c["code"]
                     and isinstance(c.get("system"), str) and c["system"].startswith("https://")
                     for c in codings), f"Observation/{i}: code.coding needs https system and code")
        need(isinstance(r.get("subject", {}).get("reference"), str), f"Observation/{i}: subject.reference required")


def check(root):
    root = Path(root)
    read = lambda rel: json.loads((root / rel).read_text(encoding="utf-8"))
    m = read("manifest.json")
    base = {s["id"]: s["baseUrl"] for s in m["shards"]}
    need(len(m["shards"]) == len(base) == 2 and len(set(base.values())) == 2
         and all(re.fullmatch(r"https://[a-z0-9-]+\.example\.org/", u) for u in base.values()),
         "expected two distinct reserved HTTPS shard bases")
    shard_of = {u: s for s, u in base.items()}

    def load(files):
        shards = {s: read(rel) for s, rel in files.items()}
        for rs in shards.values():
            for r in rs:
                shape(r)
        return shards

    def subject(idx, k):  # source-aware: a relative reference stays on its shard, an absolute one names its owner
        literal = idx[k]["subject"]["reference"]
        ref = REF.fullmatch(literal)
        need(ref is not None, f"{k[1]}: bad subject reference {literal}")
        prefix, patient_id = ref.groups()
        owner = shard_of.get(prefix) if prefix else k[0]
        need(owner and (not prefix or owner != k[0]), f"{k[1]}: bad subject reference {literal}")
        return owner, "Patient/" + patient_id

    def index(shards, unique):  # (shard, "Type/id") -> resource
        idx = {}
        for s, rs in shards.items():
            for r in rs:
                k = (s, f"{r['resourceType']}/{r['id']}")
                need(k not in idx, f"duplicate id {k[1]} on {s}")
                idx[k] = r
        need(not unique or len({t for _, t in idx}) == len(idx), "duplicate id in a unique-id corpus")
        for k in idx:
            if k[1].startswith("Observation/"):
                need(subject(idx, k) in idx, f"{k[1]}: reference does not resolve on its owning shard")
        return idx

    def select(idx, query):  # Patients chosen by the query's name/given parameter (all without one)
        p = re.search(r"\b(name|given)=(\w+)", query)
        return [k for k, r in idx.items() if r["resourceType"] == "Patient" and (not p or MATCH[p[1]](r, p[2]))]

    def chain(idx, ident, query):  # Observations whose resolved subject is a selected Patient
        chosen = set(select(idx, query))
        return sorted(ident(k) for k in idx if k[1].startswith("Observation/") and subject(idx, k) in chosen)

    def relations(idx, ident, query, forward):  # match identity -> sorted include identities
        obs = [k for k in idx if k[1].startswith("Observation/")]
        if forward:
            return {ident(k): [ident(subject(idx, k))] for k in obs}
        return {ident(p): sorted(ident(k) for k in obs if subject(idx, k) == p) for p in select(idx, query)}

    uniq = m["corpora"]["unique-id"]
    oracle = index({"oracle": load({"oracle": uniq["oracle"]})["oracle"]}, True)
    placed = {n: index(load(files), True) for n, files in uniq["placements"].items()}
    colo, cross = placed["colocated"], placed["cross-shard"]
    collision = m["corpora"]["collision"]
    coll = index(load(collision["placements"]["per-shard"]), False)
    mv = uniq["crossShardVariant"]
    tid, url = (lambda k: k[1]), (lambda k: base[k[0]] + k[1])
    ctx = {"unique-id": (oracle, tid), "collision": (coll, url)}

    relative = lambda r: {**r, "subject": {"reference": "Patient/" + REF.fullmatch(r["subject"]["reference"])[2]}} \
        if r["resourceType"] == "Observation" else r
    body = lambda idx: {t: r for (_, t), r in idx.items()}
    home = lambda idx: {t: s for (s, t) in idx}
    for idx in placed.values():
        need({t: relative(r) for t, r in body(idx).items()} == body(oracle), "placement does not normalize to the oracle")
    need(body(colo) == body(oracle), "colocated placement must use the oracle's relative references")
    moved = [t for t in home(colo) if home(colo)[t] != home(cross)[t]]
    need(moved == [mv["movedResource"]] and (home(colo)[moved[0]], home(cross)[moved[0]]) == (mv["fromShard"], mv["toShard"]),
         f"cross-shard placement must relocate exactly {mv['movedResource']}, moved {moved}")
    need([t for t in body(colo) if body(colo)[t] != body(cross)[t]] == moved
         and body(cross)[moved[0]]["subject"]["reference"] == mv["subjectReference"],
         "only the moved Observation's subject may change, to the manifest absolute reference")

    obs = sorted(t for _, t in oracle if t.startswith("Observation/"))
    orchid = chain(oracle, tid, CHAIN_QUERY)

    need({c["id"] for c in m["cases"]} == CASES and len(m["cases"]) == len(CASES), "case ids must be the named cases")
    for c in m["cases"]:
        cid = c["id"]
        if cid.startswith("chain."):
            _, placement, mode = cid.split(".")
            passthrough = mode.startswith("passthrough")
            role = "negative-control" if mode.endswith("-control") else "oracle-equivalence"
            need(c["kind"] == "chain-forward" and c["corpus"] == "unique-id"
                 and c["chainPath"] == "Observation.subject:Patient" and c["query"] == CHAIN_QUERY
                 and c["placement"] == placement and c["role"] == role
                 and c["mode"] == ("Passthrough" if passthrough else "Distributed"),
                 f"{cid}: chain query, mode, or placement changed")
            expected, idx = sorted(c["expectedMatches"]), placed[placement]
            local = {s: sorted(t for (sh, t), r in idx.items()
                               if sh == s and t in orchid and "://" not in r["subject"]["reference"])
                     for s in base}  # Passthrough keeps only locally resolvable subjects
            survivors = sorted(t for v in local.values() for t in v)
            if passthrough:
                need(c["perShardMatches"] == local and expected == survivors, f"{cid}: Passthrough answer differs from local survivors")
                need(c.get("missedVsOracle", []) == sorted(set(orchid) - set(survivors)), f"{cid}: missedVsOracle differs")
            else:
                need(expected == orchid and "perShardMatches" not in c, f"{cid}: Distributed answer must equal the oracle")
            if role == "negative-control":
                need(survivors and c["missedVsOracle"], f"{cid}: control needs a surviving and a missed witness")
            continue

        kind, corpus, query, count = SPEC[cid]
        idx, ident = ctx[corpus]
        unique = corpus == "unique-id"
        need(c["kind"] == kind and c["corpus"] == corpus and c["query"] == query and c.get("count") == count
             and (Counter(c.get("placements", [])) == Counter(placed.keys()) if unique
                  else c.get("placement") == "per-shard" and c.get("comparison") == "tagged-union"),
             f"{cid}: case shape, query, or count changed")
        expected = c["expectedMatches" if unique else "expectedFullUrls"]
        if kind == "identity-union":
            found = select(idx, query)
            need(Counter(t for _, t in found)["Patient/dup"] == 2, f"{cid}: Patient/dup must exist on both shards")
            need(Counter(expected) == Counter(ident(k) for k in found) and len(found) == 2,
                 f"{cid}: expectedFullUrls must list both owning fullUrls")
            need(c["perShardMatches"] == {s: sorted(t for sh, t in found if sh == s) for s in base}, f"{cid}: perShardMatches differ")
        elif kind == "chain-forward":
            need(c["chainPath"] == "Observation.subject:Patient" and c["mode"] == "Distributed",
                 f"{cid}: chain path or mode changed")
            need(sorted(expected) == chain(idx, ident, query), f"{cid}: Distributed answer must equal the seeds' chain result")
        else:
            forward = kind == "include-forward"
            rel = relations(idx, ident, query, forward)
            norm = lambda d: {k: sorted(v) for k, v in d.items()}
            need(sorted(expected) == sorted(rel) and norm(c["includeRelations"]) == norm(rel),
                 f"{cid}: matches or per-match include relations differ")
            if count:  # paged: the target recurs on later pages, or a match's fanout exceeds _count
                targets = Counter(t for v in rel.values() for t in v)
                need(len(rel) > count, f"{cid}: needs more matches than _count")
                if forward:
                    need(len(targets) >= 2 and max(targets.values()) >= 2, f"{cid}: needs differing and repeated targets")
                else:
                    need(max(map(len, rel.values())) > count, f"{cid}: needs an include fanout above _count")
                continue
            witnesses = set(c["coEmitted"])  # unpaged: one page must hold the discriminating witnesses together
            need(len(witnesses) >= 2 and witnesses <= set(rel), f"{cid}: needs two or more co-emitted witnesses")
            shared = sorted({t for w in witnesses for t in rel[w]})
            need(sorted(c["witnessIncludes"]) == shared, f"{cid}: witness includes differ from the witnesses' targets")
            need(sum(len(rel[w]) for w in witnesses) > len(shared), f"{cid}: witnesses must share an include target")
            if not unique:  # same Type/id owned by two shards: dedupe by Type/id would drop one include
                need(len({tuple(u.split("/")[-2:]) for u in shared}) < len(shared),
                     f"{cid}: needs two include fullUrls with the same Type/id")
    need(0 < len(orchid) < len(obs), "chain needs matching and nonmatching Observations")
    patient_ids = Counter(t for _, t in coll if t.startswith("Patient/"))
    given_by_full_url = {url(k): r["name"][0]["given"] for k, r in coll.items() if patient_ids[k[1]] > 1}
    need(collision["expectedGiven"] == given_by_full_url,
         "collision corpus: expectedGiven differs from the seeds (given names bound to fullUrls)")


def main(argv):
    try:
        check(argv[1] if len(argv) > 1 else Path(__file__).parent)
    except (AssertionError, KeyError, OSError, ValueError) as e:
        print(f"FAIL: {type(e).__name__}: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
