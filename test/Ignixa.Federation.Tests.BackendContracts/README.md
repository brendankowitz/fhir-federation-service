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
`_count=0` Patient search. It starts its own backend (a second fixture) and seeds three Patients. The two
fixtures run separately: the count collection disables parallelization, so the two SQL backends never
run at the same time.

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
collection fixture, so no third SQL start occurs. Four live cases, each independent:

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
adds one more SQL start and the count, exact-id and reference seeds are unchanged. The six seeded Patients are
synthetic, each with one `name.given` list:

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
body or a server link. Offline tests (`PatientGivenEvidenceTests`, no Docker) cover the pinned matrix, the seed,
readiness, the parser, wrong multisets, broken echoes and the request wire shape.

The Ignixa R4 host reference run (Microsoft FHIR Server for Azure, 1.0.0) historically matched and echoed all six
shapes in GET and POST. That native run is a separate reference for the literal cases only; it is not a result of
this harness and not a substitute for the Linux Docker run. No live outcome of this witness is recorded yet.

## Run it

Requires .NET SDK 11.0.100. The live tests also require a local Docker engine for `linux/amd64` containers.
The harness uses `/var/run/docker.sock` on Linux and the Docker Desktop Linux engine on Windows.
Run the same two commands as CI from the repository root:

```text
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release -- --show-live-output on --progress off --fail-skips on --filter-class '*EvidenceTests' --minimum-expected-tests 265
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release --no-build -- --show-live-output on --progress off --fail-skips on --filter-class '*ContractTests' --minimum-expected-tests 25
```

The first command builds the project and runs the 265 offline tests. The second runs the twenty-five live cases
(three reference, six count, four repeated `_id`, twelve Patient.given) using that build. The separate filters and minimum test counts prevent
offline tests from satisfying the live gate. Failed or skipped cases make their command exit non-zero.

The first live run can take several minutes to pull images and initialize the SQL schema.

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
Reports do not contain container logs, response bodies, endpoints, or credentials.

Before any case runs, the fixture waits until all five seeded Observations are returned by an `_id` search.
If readiness times out, it reports the last sanitized observation: why a response was incomplete, or how
many seeds were in a complete response. Each case then requests `_count=100` with an accurate total and fails
unless the page has no next link and the total equals the number of matches.

## Known limitation: relative references

The pinned source indexes a relative reference and an absolute reference to its own base as local.
Case 1 expects both. Case 3 checks a separate limitation: `subject=Patient/dup` does not constrain the
base and can include foreign references, so it is not an exact-local query.

## Evidence status

- Local (Windows, no Docker engine): the project builds with the .NET 11.0.100 SDK. The offline tests pass.
  The twenty-five live cases (three reference, six count, four repeated `_id`, twelve Patient.given) run and fail
  with `[prerequisite]`, as they should without Docker: the offline run is 265 passed, 0 failed, 0 skipped,
  and the live run is 25 total, 0 passed, 25 failed, 0 skipped.
- Pending Docker or CI: startup, port binding, match sets, the Patient count totals, the repeated
  `_id` match and echo outcomes, and the Patient.given match and echo outcomes have not been verified against a
  live backend; no outcome is recorded here and none is assumed. The first Linux run in
  `.github/workflows/backend-contracts.yml` supplies that evidence. Until then the count and Patient.given
  witnesses are not accepted.
