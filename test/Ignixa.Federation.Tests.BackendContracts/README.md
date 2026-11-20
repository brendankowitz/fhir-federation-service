# Backend contract tests

These tests run one real Microsoft FHIR Server R4 on SQL Server and record how it answers reference
searches. They produce the evidence behind design §4.1 (identity and references), §7.5 (distributed
chain rewrite) and §7.6 (includes and revincludes) of
[the design](../../docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md).

This is not a profile certification and not a federation end-to-end test. No federation code runs here.

## What is tested

One backend is started and seeded once. The three cases share it and report separately.

Seed (all ids are reserved synthetic ids):

| Observation | `subject.reference` |
| --- | --- |
| `obs-relative-dup` | `Patient/dup` |
| `obs-own-absolute-dup` | `<local base>/Patient/dup`, where the local base is the URL this test client uses |
| `obs-foreign-absolute-dup` | `https://foreign-shard.invalid/Patient/dup` (never contacted) |
| `obs-control-other` | `Patient/other` |

| Case | Label | Query | Expected matches |
| --- | --- | --- | --- |
| 1 | contract | `subject=<local base>/Patient/dup` | `obs-own-absolute-dup`, `obs-relative-dup` |
| 2 | contract | `subject=https://foreign-shard.invalid/Patient/dup` | `obs-foreign-absolute-dup` |
| 3 | characterization | `subject=Patient/dup` | all three `dup` observations |

A **contract** case states what the federation relies on. A **characterization** case records what the
backend does. Case 3 is not interchangeable with case 1: a type-and-id query admits foreign references
with the same id, so the federation must not use it to mean "local".

Expected ids are literals in the test, compared as sorted multisets. The expected outcomes follow the
backend source at release 5.2.84 (`ReferenceSearchValueParser`, `SearchValueExpressionBuilderHelper`).

The suite also holds offline tests for the comparison itself. They need no Docker and show that the
two documented mistakes are caught:

- treating a host-qualified query as type-and-id only, which admits the foreign reference;
- comparing the reference text literally, which drops the relative reference from case 1.

## Run it

Requires .NET SDK 11.0.100 and a local Docker engine for `linux/amd64` containers. The harness uses
`/var/run/docker.sock` on Linux and the Docker Desktop Linux engine on Windows. From the repository root:

```text
dotnet run --project test/Ignixa.Federation.Tests.BackendContracts/Ignixa.Federation.Tests.BackendContracts.csproj --configuration Release -- --show-live-output on --progress off --minimum-expected-tests 24
```

The command exits non-zero if a case fails. To run only the offline tests, replace the options after
`--` with `--filter-class Ignixa.Federation.Tests.BackendContracts.BackendEvidenceTests`.
The first live run can take several minutes to pull images and initialize the SQL schema.

`dotnet test` does not work for this project until the repository opts in to the test platform runner
at the root. Use `dotnet run` as above.

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

Before any case runs, the fixture waits until all four seeded Observations are returned by an `_id` search. Each
case then requests `_count=100` with an accurate total, and it fails unless the page has no next link and the total
equals the number of matches.

## Known limitation: relative references

The pinned source indexes a relative reference and an absolute reference to its own base as local.
Case 1 expects both. Case 3 checks a separate limitation: `subject=Patient/dup` does not constrain the
base and can include foreign references. Do not treat it as an exact-local filter.

## Evidence status

- Local (Windows, no Docker engine): the project builds with the .NET 11.0.100 SDK. The offline tests pass.
  The three live cases run and fail with `[prerequisite]`, as they should without Docker.
- Pending CI: startup, port binding, and match sets have not been verified against the live pair.
  The first Linux run in `.github/workflows/backend-contracts.yml` supplies that evidence.
