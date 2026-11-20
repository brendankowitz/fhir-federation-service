#!/usr/bin/env python3
"""Offline checks for the federation-oracle fixtures (stdlib only).

Verifies seed integrity and the seven named cases in manifest.json against the
seeds. It is not a FHIR validator or search engine. Usage: check_fixtures.py [root]
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
CASES = {"chain.colocated.passthrough", "chain.colocated.distributed",
         "chain.cross-shard.passthrough-control", "chain.cross-shard.distributed",
         "collision.tagged-union", "include.forward", "include.reverse"}
CHAIN_QUERY = "Observation?subject:Patient.name=Orchid"
INCLUDE_QUERIES = {
    "include.forward": "Observation?_include=Observation:subject:Patient&_count=1",
    "include.reverse": "Patient?_revinclude=Observation:subject:Patient&_count=1",
}


def need(ok, msg):
    if not ok:
        raise AssertionError(msg)


def shape(r):
    t, i = r.get("resourceType"), r.get("id")
    need(t in KEYS and set(r) <= KEYS[t], f"unknown resource shape {t}/{i}")
    need(isinstance(i, str) and re.fullmatch(r"[A-Za-z0-9.-]{1,64}", i), f"invalid id {i!r}")
    if t == "Patient":
        need(isinstance(r.get("active"), bool), f"Patient/{i}: active must be boolean")
        names = r.get("name")
        need(isinstance(names, list) and len(names) == 1, f"Patient/{i}: expected one name")
        name = names[0]
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

    def index(shards):  # "Type/id" -> (shard, resource); ids are unique in this corpus
        idx = {}
        for s, rs in shards.items():
            for r in rs:
                k = f"{r['resourceType']}/{r['id']}"
                need(k not in idx, f"duplicate id {k}")
                idx[k] = (s, r)
        for k, (s, r) in idx.items():  # each subject is local, or absolute and owned by another shard
            if r["resourceType"] != "Observation":
                continue
            literal = r["subject"]["reference"]
            ref = REF.fullmatch(literal)
            need(ref is not None, f"{k}: bad subject reference {literal}")
            prefix, patient_id = ref.groups()
            home = shard_of.get(prefix) if prefix else s
            need(home and (not prefix or home != s), f"{k}: bad subject reference {literal}")
            target = idx.get(f"Patient/{patient_id}")
            need(target and target[0] == home, f"{k}: reference does not resolve on its owning shard")
        return idx

    def relative(r):  # drop the base from an absolute subject reference
        return {**r, "subject": {"reference": "Patient/" + REF.fullmatch(r["subject"]["reference"])[2]}} \
            if r["resourceType"] == "Observation" else r

    uniq = m["corpora"]["unique-id"]
    oracle = index({"oracle": load({"oracle": uniq["oracle"]})["oracle"]})
    placed = {n: index(load(files)) for n, files in uniq["placements"].items()}
    colo, cross = placed["colocated"], placed["cross-shard"]
    mv = uniq["crossShardVariant"]

    for idx in placed.values():
        need({k: relative(r) for k, (_, r) in idx.items()} == {k: r for k, (_, r) in oracle.items()},
             "placement does not normalize to the oracle")
    need({k: r for k, (_, r) in colo.items()} == {k: r for k, (_, r) in oracle.items()},
         "colocated placement must use the oracle's relative references")
    moved = [k for k in colo if colo[k][0] != cross[k][0]]
    need(moved == [mv["movedResource"]] and (colo[moved[0]][0], cross[moved[0]][0]) == (mv["fromShard"], mv["toShard"]),
         f"cross-shard placement must relocate exactly {mv['movedResource']}, moved {moved}")
    need([k for k in colo if colo[k][1] != cross[k][1]] == moved
         and cross[moved[0]][1]["subject"]["reference"] == mv["subjectReference"],
         "only the moved Observation's subject may change, to the manifest absolute reference")

    fam = lambda idx, k: idx[k][1]["name"][0]["family"]
    obs = sorted(k for k in oracle if k.startswith("Observation/"))
    subject = lambda idx, k: "Patient/" + REF.fullmatch(idx[k][1]["subject"]["reference"])[2]
    orchid = [k for k in obs if fam(oracle, subject(oracle, k)) == "Orchid"]
    need(0 < len(orchid) < len(obs), "chain needs matching and nonmatching Observations")

    need({c["id"] for c in m["cases"]} == CASES and len(m["cases"]) == len(CASES), "case ids must be the seven named cases")
    for c in m["cases"]:
        cid, expected = c["id"], c.get("expectedMatches")
        if cid.startswith("chain."):
            _, placement, mode = cid.split(".")
            passthrough = mode.startswith("passthrough")
            role = "negative-control" if mode.endswith("-control") else "oracle-equivalence"
            need(c["kind"] == "chain-forward" and c["corpus"] == "unique-id"
                 and c["chainPath"] == "Observation.subject:Patient" and c["query"] == CHAIN_QUERY
                 and c["placement"] == placement and c["role"] == role
                 and c["mode"] == ("Passthrough" if passthrough else "Distributed"),
                 f"{cid}: chain query, mode, or placement changed")
            idx = placed[c["placement"]]
            local = {s: sorted(k for k in obs if idx[k][0] == s and k in orchid and "://" not in idx[k][1]["subject"]["reference"])
                     for s in base}  # Passthrough keeps only locally resolvable subjects
            survivors = sorted(k for v in local.values() for k in v)
            if passthrough:
                need(c["perShardMatches"] == local and expected == survivors, f"{cid}: Passthrough answer differs from local survivors")
                need(c.get("missedVsOracle", []) == sorted(set(orchid) - set(survivors)), f"{cid}: missedVsOracle differs")
            else:
                need(expected == orchid and "perShardMatches" not in c, f"{cid}: Distributed answer must equal the oracle")
            if role == "negative-control":
                need(survivors and c["missedVsOracle"], f"{cid}: control needs a surviving and a missed witness")
            else:
                need(expected == orchid, f"{cid}: oracle-equivalence answer differs from the oracle")
        elif cid == "collision.tagged-union":
            need(c["kind"] == "identity-union" and c["corpus"] == "collision"
                 and c["placement"] == "per-shard" and c["comparison"] == "tagged-union"
                 and c["query"] == "Patient?name=Orchid", f"{cid}: collision query or comparison changed")
            shards = load(m["corpora"]["collision"]["placements"]["per-shard"])
            ids = Counter(r["id"] for rs in shards.values() for r in rs if r["resourceType"] == "Patient")
            need(ids["dup"] == 2 and len(shards) == 2, f"{cid}: Patient/dup must exist on both shards")
            full = Counter(base[s] + "Patient/" + r["id"] for s, rs in shards.items() for r in rs if r["name"][0]["family"] == "Orchid")
            need(Counter(c["expectedFullUrls"]) == full and len(full) == 2, f"{cid}: expectedFullUrls must list both owning fullUrls")
            need(c["perShardMatches"] == {s: ["Patient/dup"] for s in base}, f"{cid}: perShardMatches differ")
        else:
            fwd = cid == "include.forward"
            matches = obs if fwd else sorted(k for k in oracle if k.startswith("Patient/"))
            rel = {k: [subject(oracle, k)] for k in obs} if fwd else \
                {p: [k for k in obs if subject(oracle, k) == p] for p in matches}
            need(c["kind"] == ("include-forward" if fwd else "include-reverse")
                 and c["corpus"] == "unique-id" and Counter(c["placements"]) == Counter(placed.keys())
                 and c["query"] == INCLUDE_QUERIES[cid] and c["count"] == 1,
                 f"{cid}: needs both placements and the paged include query")
            need(expected == matches and c["includeRelations"] == rel, f"{cid}: matches or per-match include relations differ")
            targets = Counter(t for v in rel.values() for t in v)
            if fwd:
                need(len(targets) >= 2 and max(targets.values()) >= 2, f"{cid}: needs differing and repeated targets")
            else:
                need(max(map(len, rel.values())) > c["count"], f"{cid}: needs an include fanout above _count")


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
