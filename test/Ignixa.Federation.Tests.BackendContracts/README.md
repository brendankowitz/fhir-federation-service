# Backend contract tests

These tests run real Microsoft FHIR Server R4 on SQL Server and record how it answers reference
searches and ordinary count requests. The reference witness covers design §4.1 (identity and references),
§7.5 (distributed chain rewrite) and §7.6 (includes and revincludes) of
[the design](../../docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md).

This is not a profile certification and not a federation end-to-end test. No federation code runs here.

## Reference search witness

One backend is started and seeded once. The three cases share it and report separately.

Seed (all ids are reserved synthetic ids). Foreign A and foreign B each replace only the hostname of
the actual local base, keeping its scheme, port, and path. This tests that the backend distinguishes
the hosts, not just the schemes or ports. Both values are reference text only; neither host is contacted.

| Observation | `subject.reference` |
| --- | --- |
| `obs-relative-dup` | `Patient/dup` |
| `obs-own-absolute-dup` | `<local base>/Patient/dup`, where the local base is the URL this test client uses |
| `obs-foreign-absolute-dup` (foreign A) | `<local base with host foreign-shard.invalid>/Patient/dup` |
| `obs-foreign-b-absolute-dup` (foreign B) | `<local base with host foreign-shard-b.invalid>/Patient/dup` |
| `obs-control-other` | `Patient/other` |

| Case | Label | Query | Expected matches |
| --- | --- | --- | --- |
| 1 | contract | `subject=<local base>/Patient/dup` | `obs-own-absolute-dup`, `obs-relative-dup` |
| 2 | contract | `subject=<foreign A base>/Patient/dup` | `obs-foreign-absolute-dup` only |
| 3 | characterization | `subject=Patient/dup` | all four `dup` observations |

A **contract** case states what the federation relies on. A **characterization** case records what the
backend does. The requirement is a profile-certified exact-local query: the backend must answer a
host-qualified query for its own base with the relative and own-absolute references and no foreign
reference (case 1). Case 3 characterizes the relative spelling `subject=Patient/dup`. It does not constrain
the base, so it admits both foreign references. It is not a way to make the contract pass, and the federation
must not use it to mean "local". Case 2 must exclude foreign B, so a backend that ignores the host fails it.

Expected ids are literals in the test, compared as sorted multisets. The expected outcomes follow the
backend source at release 5.2.84 (`ReferenceSearchValueParser`, `SearchValueExpressionBuilderHelper`).

The suite also holds offline tests for the comparison itself. They need no Docker and show that these
mistakes are caught:

- treating a host-qualified query as type-and-id only, which admits the foreign references in case 1;
- comparing the reference text literally, which drops the relative reference from case 1;
- ignoring the host in a foreign query, which returns foreign B for case 2. This control uses the same
  comparison as the live case.

An offline fixture check also verifies each observation's outgoing `subject.reference` spelling.
It does not require the server to preserve that spelling on read-back.

## Patient count witness (`_count=0`)

A second, separate witness records whether the backend reports an exact filtered `total` for a
`_count=0` Patient search. It starts its own backend (a second fixture) and seeds three Patients. Overall the
live suite has three isolated, serial SQL/FHIR fixtures (reference, count and Patient.given). The count and
Patient.given collections disable parallelization so no two SQL backends run at the same time.

| Patient | `gender` |
| --- | --- |
| `count-female-1` | `female` |
| `count-female-2` | `female` |
| `count-male-1` | `male` |

| Method | Filter | Expected `total` |
| --- | --- | --- |
| GET, POST | none | 3 |
| GET, POST | `gender=female` | 2 |
| GET, POST | `gender=unknown` | 0 |

Six live cases (two theories of three rows). Expected totals are literals in the test. Both filtered
rows catch a backend that ignores the filter and returns 3. Exact query shapes:

- GET: `Patient?_count=0` or `Patient?_count=0&gender=<value>`
- POST: `Patient/_search` with an `application/x-www-form-urlencoded` body `_count=0` or
  `_count=0&gender=<value>`

Both send `Prefer: handling=strict` and no `_total` parameter, so the answer is the backend's own
default behavior. A response counts as evidence only if it is a searchset with no entries, no `next`
link and a present non-negative integer `total`; anything else is `[incomplete-search]`. A well-formed
but different total is `[contract]`; HTTP 400, 422 or 501 is `[unsupported-capability]`.

Readiness for this backend still uses the positive `_id` search for the three seeded Patients. A count
query never gates readiness. Offline tests (`PatientCountEvidenceTests`, no Docker) cover the parser,
the sanitized report, the request construction, the seed shape, and the generalized readiness.

## Repeated decoded `_id` (`RepeatedIdContractTests`)

A third witness records how the backend handles the same `_id` sent twice. The two sent occurrences are
one plain spelling and one raw-escaped spelling that decode to the same name and value:
`_id=count-female-1` and `%5Fid=%63ount-female-1`. It reuses the seeded count backend through a shared
collection fixture, so it adds no SQL/FHIR start of its own. Four live cases, each independent:

| Method | Shape | Controls (after the pair) | Match contract |
| --- | --- | --- | --- |
| GET, POST | primary | `_count=100&_total=accurate` | exactly the seeded Patient `count-female-1`, `total` 1, no `next` link |
| GET, POST | count | `_count=0` | `total` 1, no entries, no `next` link |

GET is `Patient?<pair>&<controls>`; POST is `Patient/_search` with the same text as an
`application/x-www-form-urlencoded` body. Both send `Prefer: handling=strict`.

Each case reports two things apart:

- **Match** is a contract. A complete answer with a different Patient or total is `[contract]`; an answer
  that cannot be accounted for (next link, entries on a count, a wrong or missing `_id` in the echo, or
  more than one `self` link) is `[incomplete-search]`; HTTP 400, 422 or 501 is `[unsupported-capability]`
  and names the method and shape.
- **`Bundle.self` echo** is a characterization only. One decoded `_id` occurrence is a collapsed pair and
  two is an echoed pair; either passes. A collapsed pair does not prove the predicates were preserved,
  and an echoed pair does not show that the identical predicates independently affected the result.
  The report contains counts only, never the query text or response body.

The raw escape spelling must reach the wire. .NET canonicalizes a GET query and silently unescapes
`%5F` and `%63`, which would send `_id=…&_id=…`. The GET request therefore uses an absolute URI created
with `DangerousDisablePathAndQueryCanonicalization`, and an offline test captures the real request through
a loopback server to prove the spelling pair is on the wire. The POST body is sent raw.

No live outcome is recorded yet: the match and echo results for GET and POST are pending the first Linux
run. Offline tests (`RepeatedIdEvidenceTests`, no Docker) cover the parser, sanitized reporting, and
the actual request target and form body sent by the client.

## Patient.given witness (`PatientGivenContractTests`)

A fourth witness records how the backend handles `Patient.given` string search: a default match, a second
value, the AND of a repeated parameter, the OR of a comma list, and `:exact`. It has its own disposable seed
and its own live fixture and collection (`PatientGivenBackendFixture`, `LiveGivenBackendDefinition`), so it
is the third isolated serial SQL/FHIR fixture; the count, repeated `_id` and reference seeds are unchanged.
The six seeded Patients are synthetic, each with one `name.given` list:

| id | given |
| --- | --- |
| `given-both` | Quartz, Mica |
| `given-left` | Quartz |
| `given-right` | Mica |
| `given-long` | Quartzite |
| `given-case` | QUARTZ |
| `given-neither` | Topaz |

Twelve live cases: each of the six below, as GET and as a form POST.

| Case | Selection text (escapes literal) | Required matches |
| --- | --- | --- |
| `left` | `given=Quartz` | `given-both`, `given-case`, `given-left`, `given-long` |
| `right` | `given=Mica` | `given-both`, `given-right` |
| `and` | `given=Quartz&given=Mica` | `given-both` |
| `or` | `given=Quartz%2CMica` | `given-both`, `given-case`, `given-left`, `given-long`, `given-right` |
| `exact` | `given%3Aexact=Quartz` | `given-both`, `given-left` |
| `exact-upper` | `given%3Aexact=QUARTZ` | `given-case` |

The expected ids are literals derived from FHIR R4 string search, never from a response. Every request appends
`&_count=100&_total=accurate` and sends `Prefer: handling=strict`. GET is `Patient?<selection>&<controls>`; POST
is `Patient/_search` with the same text as an `application/x-www-form-urlencoded` body. Both are sent raw, so
`%2C` and `%3A` reach the wire as written (the GET target disables .NET query canonicalization, and an offline
test captures the real request through a loopback server).

Each case is a contract on two things, reported apart:

- **Match**: the sorted ids of one complete page (no next link, total equal to the entries, Patient matches
  only) equal the literal expectation as a multiset. A different set is `[contract]`; an answer that cannot be
  accounted for is `[incomplete-search]`; HTTP 400, 422 or 501 is `[unsupported-capability]`.
- **`Bundle.self` echo**: the decoded name/value pairs of the single self link, ignoring `_count` and
  `_total`, equal the decoded selection pairs with multiplicity, so a dropped, altered, added or collapsed
  predicate fails even when the match is correct, and a correct echo never excuses a wrong match. Any spelling
  that decodes the same is accepted.

Reports print reserved ids and the expected pairs only, plus a count of unexpected pairs, never the response
body or a server link. Offline tests (`PatientGivenEvidenceTests`, no Docker) verify seed isolation, readiness,
and the literal GET and POST wire for AND, OR and `:exact`. They reject incomplete pages, wrong match
multisets despite correct echoes, and wrong echoes despite correct matches.

Historical reference only: a separate native Windows LocalDB run of the Microsoft FHIR Server (the "west"
build, not Ignixa) reported `FHIR Server for Azure` / `1.0.0` in its metadata. All six shapes returned the
expected match multisets for both methods with `_count=20` and no `_total=accurate`. Every required pair
appeared in `Bundle.self`, but the native echo check did not rule out extra pairs. The match multiset, not
the echo, is the evidence that the tested predicates took effect. That run is not this harness, not the pinned
`r4-fhir-server:5.2.84` image and not a Linux Docker run, so it is not evidence for this suite. No live
outcome of this witness is recorded yet.

## Repeated Observation.subject join witness (`ReferenceSearchContractTests`)

A fifth witness extends the reference witness. It reuses the existing reference fixture and seed (so no extra
backend start) and records how the backend answers `Observation?subject=...` when `subject` is given twice.
Six live cases are added: `equal` and `unequal` as GET, and `foreign-single`, `own-single`, `equal` and
`unequal` as form POST. The two single-term POST cases are positive controls on the same method.

| Case | Selection (each `subject` is the full reference) | Required matches |
| --- | --- | --- |
| `foreign-single` (POST only) | foreign A `Patient/dup` | `obs-foreign-absolute-dup` |
| `own-single` (POST only) | own-absolute `Patient/dup` | `obs-own-absolute-dup`, `obs-relative-dup` |
| `equal` | foreign A twice, spelled `subject` and `%73ubject`, second value with `%50atient` | `obs-foreign-absolute-dup` |
| `unequal` | foreign A, then own-absolute `Patient/dup` | none |

The expected ids are written from the requirement and the reserved seed ids, never from a response. The two `equal`
spellings differ as raw text and decode to the same pair. Every request appends `&_count=100&_total=accurate` and
sends `Prefer: handling=strict`. GET is `Observation?<selection>&<controls>` with canonicalization disabled; POST
is `Observation/_search` with the same text as a raw form body.

Each case reports two facts apart, and neither excuses the other:

- **Match** (`[contract]`, every case and method): the sorted ids of one complete page equal the table.
- **`Bundle.self` echo**: the decoded pairs, ignoring only `_count` and `_total`, compared with the requested
  terms as a multiset. GET must echo every occurrence, so a collapsed, dropped, altered, tripled or added pair
  fails. A form POST of `equal` is **characterization only**: both one occurrence (collapsed) and two (echoed)
  pass and are reported as characterization. Zero, three, wrong, missing or extra terms fail, and distinct
  terms must each echo once.

Accepting either POST echo records what a backend does. It neither says the predicates independently affected the
result nor preserves or authorizes POST for production joins. Reports print reserved ids, term labels and counts,
never a reference value, query or response text. Offline tests (`SubjectJoinEvidenceTests`, no Docker) cover the
seed and raw-spelling tie, the match and echo rules against each other, the one and two POST echoes, spelling and
order, representative unaccountable pages, sanitized reports, and the real GET and POST wire through a loopback
server.

Coordinator-reported, not reproduced here: a separate native run (not this harness, not the pinned image, and
without a software identity recorded in this repository) selected the matches in the table for both methods. GET
echoed both occurrences for `equal` and `unequal`; POST echoed one occurrence for `equal` and both for `unequal`.
That is not evidence for this suite and not acceptance. No live outcome of this witness is recorded yet.

## Directional sort and continuation witness (`PatientContinuationContractTests`)

A sixth witness records, over FHIR HTTP only, how the backend orders and pages `Patient?_sort=_lastUpdated` and
`Patient?_sort=-_lastUpdated` with `_count=1`, and whether it keeps a continuation stable when its `next` link is
redeemed again. Four live cases are added, each a separate case: GET and form POST, ascending and descending. It
reuses the three-Patient count fixture and seed, so no extra backend start and no write during the walk.

- **Oracle.** Three direct reads of the reserved Patients give each one's id, version and update instant. The
  expected order is those instants, ascending or reversed. It is never taken from a search, from seed order or
  from id order. The seed pauses 50 ms after each write so the instants differ. If two are equal the walk fails
  as `[incomplete-search]`; a tie is a failure of the evidence, never a skip or a pass.
- **Walk.** The start page is the one-row page of the sort. A GET follows each `next` link verbatim; a POST
  re-posts the link's query suffix to `Patient/_search` as the form body. The suffix is replayed exactly as issued:
  never decoded, split, reordered or merged. Each distinct issued link is redeemed twice, and both redemptions must
  hold exactly the one Patient the oracle expects at that position with the same version and instant. A link
  already issued for an earlier position is never redeemed again: reissuing one is a `[contract]` failure and
  stops the walk, because a backend that advances state per redemption could otherwise look stable.
- **Termination.** After the third row, an absent `next` or one empty terminal page with no `next` and no rows is
  accepted. A missing, extra, repeated, backward or earlier-ending row, or a `next` after the terminal page, is a
  `[contract]` failure. The walk sends at most 15 searches plus the three reads and never repeats a link a
  third time.
- **Safety.** A `next` link is accepted only on the exact origin of the fixture (`http://127.0.0.1:<port>`), the
  `Patient` path and a non-empty plain query whose `%` escapes are each followed by two hex digits. Otherwise
  the page is not evidence and nothing further is sent.
  Reports print reserved ids, versions, instants and counts, never a link, token or response text. A version is
  printed only after it matches a bounded safe token (at most 64 letters, digits, `-` or `.`).

It makes no claim about tied instants, other queries or the token text. Offline tests
(`PatientContinuationEvidenceTests`, no Docker) drive the real client and walk against a scripted loopback
backend that accepts only the exact wire shape, and check the replay of an odd encoded suffix on each verb, double
redemption, token reuse across positions, terminal handling, mismatches in row, version and instant, the origin,
path and escape rejections, and report sanitization. Mutations of the double redemption, the tie rule, the
version and instant comparison, the terminal rule, direction, oracle order, the origin check, token reuse and
verbatim replay each fail them. No live outcome of this
witness is recorded, and it is not accepted until a Linux run supplies one.

## Run it

Requires .NET SDK 11.0.100. The live tests also require a local Docker engine for `linux/amd64` containers.
The harness uses `/var/run/docker.sock` on Linux and the Docker Desktop Linux engine on Windows.
Run the same two commands as CI from the repository root:

```text
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release -- --show-live-output on --progress off --fail-skips on --filter-class '*EvidenceTests' --minimum-expected-tests 302
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release --no-build -- --show-live-output on --progress off --fail-skips on --filter-class '*ContractTests' --minimum-expected-tests 35
```

The first command builds the project and runs the offline evidence tests. The second runs the thirty-five live
cases (three reference, six count, four repeated `_id`, twelve Patient.given, six Observation.subject join,
four directional continuation)
using that build. The separate
filters and minimum test counts prevent offline tests from satisfying the live gate. Failed or skipped cases
make their command exit non-zero.

The first live run can take several minutes to pull images and initialize the SQL schema. Each of the three
serial fixtures has a 12-minute startup budget, so the CI job has a 60-minute ceiling: 36 minutes of startup
plus the bounded requests, teardown and build.

The project is intentionally outside the main solution; use the standalone commands above.

## Constraints

- Images are pinned by digest:
  `mcr.microsoft.com/healthcareapis/r4-fhir-server:5.2.84` and `mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04`.
  This test targets SQL Server 2022-CU28, not the SQL Server 2025 image in the upstream example.
  Running the test accepts the SQL Server licence terms (`ACCEPT_EULA=Y`).
- A private Docker network is created per run. SQL Server is not published. Only the FHIR port is published, on
  `127.0.0.1` with a Docker-assigned port. Resource names and the SQL password are unique per run.
- The test sends FHIR traffic only to the loopback port. Restore and setup may download packages and
  images. The server runs without authentication. Its required authority is a reserved `.invalid` URL.
- Redirects and proxies are off. Startup polls, each HTTP request and the whole startup are time-bounded.
- The fixture removes its containers and network on success or failure. It does not give a helper
  container the host Docker socket. Killing the process can leave resources named `fhir-backend-contract-*`.
- There is no switch that skips the tests. Without Docker they fail.

## Reading a failure

Every failure starts with a tag, so a missing prerequisite is never mistaken for a backend answer.

| Tag | Meaning |
| --- | --- |
| `[prerequisite]` | No reachable Docker engine. |
| `[startup]` | A container, the FHIR server or seeding failed or timed out. |
| `[incomplete-search]` | A response cannot be accounted for: a next link, a total that does not match the entries, a non-match entry, or seeded data not yet searchable. It is not evidence either way. |
| `[unsupported-capability]` | The backend rejected the search (HTTP 400, 422 or 501). |
| `[contract]` | A complete search returned a different multiset. The backend does not support the required behavior in this case. |
| `[characterization]` | A complete relative search differed from the source-based expectation. |
| `[cleanup]` | The fixture could not remove a container or network after the searches. Cleanup failures after startup errors are appended to the startup failure. |

Reports identify the backend using the bounded `software.name` and `software.version` fields from
`GET /metadata`, not the image tag. Search diagnostics contain only reserved ids, counts, status
codes, and exception type names. Startup failures may also report container state and exit code.
On fixture startup failure, the harness reports the loopback metadata URL and last probe error and
prints bounded tails from the two disposable containers before removing them. The generated SQL
password is redacted. Probe response bodies are not printed.

Before any case runs, the fixture waits until all five seeded Observations are returned by an `_id` search.
If readiness times out, it reports the last sanitized observation: why a response was incomplete, or how
many seeds were in a complete response. Each case then requests `_count=100` with an accurate total and fails
unless the page has no next link and the total equals the number of matches. Duplicate JSON members are
rejected before those checks, so a second `link` member cannot hide a continuation.

## Known limitation: relative references

The pinned source indexes a relative reference and an absolute reference to its own base as local.
Case 1 expects both. Case 3 checks a separate limitation: `subject=Patient/dup` does not constrain the
base and can include foreign references, so it is not an exact-local query.

## Evidence status

- Local (Windows, no Docker engine): the project builds with the .NET 11.0.100 SDK. The offline tests pass.
  The thirty-five live cases (three reference, six count, four repeated `_id`, twelve Patient.given, six
  Observation.subject join, four directional continuation) run and fail with `[prerequisite]`, as they should
  without Docker: the offline run is 302 passed, 0 failed, 0 skipped, and the live run is 35 total, 0 passed,
  35 failed, 0 skipped.
- Pending Docker or CI: startup, port binding, match sets, the Patient count totals, the repeated
  `_id` match and echo outcomes, the Patient.given match and echo outcomes, the Observation.subject join
  match and echo outcomes, and the directional continuation outcomes
  have not been verified against a live backend; no outcome is recorded here and none is
  assumed. The first Linux run in `.github/workflows/backend-contracts.yml` supplies that evidence. Until then
  the count, Patient.given, Observation.subject join and continuation witnesses are not accepted.
