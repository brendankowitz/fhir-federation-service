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

## Run it

Requires .NET SDK 11.0.100. The live tests also require a local Docker engine for `linux/amd64` containers.
The harness uses `/var/run/docker.sock` on Linux and the Docker Desktop Linux engine on Windows.
Run the same two commands as CI from the repository root:

```text
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release -- --show-live-output on --progress off --fail-skips on --filter-class '*EvidenceTests' --minimum-expected-tests 78
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release --no-build -- --show-live-output on --progress off --fail-skips on --filter-class '*ContractTests' --minimum-expected-tests 9
```

The first command builds the project and runs the 78 offline tests. The second runs the nine live cases
(three reference, six count) using that build. The separate filters and minimum test counts prevent
offline tests from satisfying the live gate. Failed or skipped cases make their command exit non-zero.

The first live run can take several minutes to pull images and initialize the SQL schema.

The project is intentionally outside the main solution; use the standalone command above.

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
  The nine live cases (three reference, six count) run and fail with `[prerequisite]`, as they should
  without Docker: 87 total, 78 passed, 9 failed, 0 skipped.
- Pending Docker or CI: startup, port binding, match sets and the Patient count totals have not been
  verified against a live backend. The first Linux run in `.github/workflows/backend-contracts.yml`
  supplies that evidence. Until then the count witness is not accepted.
