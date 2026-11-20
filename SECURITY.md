# Security policy

## Supported versions

Ignixa Federation is pre-release. Only the `main` branch receives security fixes.

## Report a vulnerability

Report vulnerabilities privately through a [GitHub security advisory](https://github.com/brendankowitz/fhir-federation-service/security/advisories/new). Do not open a public issue or pull request for a vulnerability.

Include:

* The affected commit or version.
* A description of the vulnerability and its impact.
* Steps or a proof of concept that reproduces it.
* Whether you know of a fix or mitigation.

Do not include real patient data or real credentials in a report.

## Scope

The design spec sets these security boundaries. A way around any of them is in scope.

* **Read-only.** The gateway never writes to a shard. Write requests are refused ([spec §2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#2-goals-non-goals-anti-goals)).
* **No SMART or caller-token passthrough.** The gateway refuses tokens that carry SMART scopes with 403. Callers authorise with role claims only, and backends see only the federation's own read-only credential ([spec §10.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#101-caller-authorisation-d2), [§10.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#102-backend-credentials)).
* **Credentials stay with their shard.** A shard credential goes only to requests that match that shard's base URL. The gateway does not follow redirects ([spec §10.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#102-backend-credentials)).

The authentication and authorisation code is not implemented yet. See the status table in the [README](README.md#project-status-by-capability).
