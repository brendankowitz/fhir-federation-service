# Review criteria

Reviewers check every pull request against the spec invariants, the principles, and the repository checks below. The spec is [docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md). A change that breaks an invariant needs a spec change, approved by the owner, in the same pull request.

## Spec invariants

- [ ] **Read-only.** No code path writes to a shard. Create, update, delete, patch, batch, transaction, and `$operation` requests are refused with an OperationOutcome ([§2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#2-goals-non-goals-anti-goals), [§5](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#5-request-surface)).
- [ ] **No partial results.** A needed shard that fails, times out, or returns a malformed Bundle fails the request with 502, 503, or 504. The code never returns a Bundle with a shard silently missing ([§2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#2-goals-non-goals-anti-goals), [§8](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#8-failure-semantics)).
- [ ] **No silently dropped filters.** An unsupported parameter or modifier returns 400. Backend searches carry `Prefer: handling=strict` ([§6.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#61-principle-plan-on-the-parse-tree-execute-on-raw-parameter-occurrences), [§6.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#62-backend-strictness-fail-closed-on-dropped-filters)).
- [ ] **SMART refused.** A token that carries a SMART scope gets 403. Authorisation uses role claims only ([§10.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#101-caller-authorisation-d2)).
- [ ] **Credentials stay with their shard.** A credential goes only to a request whose scheme, host, port, and base path match that shard. Callers' tokens never go to a backend ([§10.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#102-backend-credentials)).
- [ ] **No redirects followed to shards.** Backend clients do not follow redirects ([§8](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#8-failure-semantics), [§10.2](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#102-backend-credentials)).
- [ ] **Identity is `(shard, type, id)`.** `fullUrl` is the resource's URL on its shard, and resource content is not modified ([§4.1](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#41-model-server-scoped-identity-fhir-standard-references-d1), [§11](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#11-response-shaping)).
- [ ] **Bounded work.** Backend calls, bytes, and time have a limit, and exceeding it fails the request ([§7.8](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md#78-runtime-budget)).

## Principles

Each item comes from a pstack principle skill.

- [ ] **Model the domain.** A data structure carries the domain rule instead of a growing chain of conditionals or paired booleans.
- [ ] **Boundary discipline.** Validation and error handling sit where data enters the system. Inside, the code trusts its types.
- [ ] **Type system discipline.** Illegal states do not compile. Semantic primitives have their own types, and external data is parsed once at the boundary.
- [ ] **Test behaviour, not implementation.** Each test fails when the observable behaviour breaks and survives an internal refactor.
- [ ] **Prove it works.** The pull request shows real command output, not "it compiles".
- [ ] **Subtract before you add.** The change removes dead code and unneeded guards before it adds new code.
- [ ] **Minimize reader load.** No wrapper with one caller, no adapter with one implementation, and mutable state has the smallest scope that works.
- [ ] **Sequence verifiable units.** The change is one unit that a reviewer can check on its own, and the pull request body says how.
- [ ] **Make operations idempotent.** A step that runs twice, or after a crash, converges to the same state.

## Repository checks

- [ ] **Boundaries hold.** `Core` has no HTTP or hosting dependency. `Shards` has no ASP.NET dependency. `Api` holds the hosting code.
- [ ] **Naming follows [AGENTS.md](AGENTS.md).** The `Ignixa.Federation` prefix, namespaces that mirror folders, no identifier that starts with `Fhir` or `FHIR`, the `Federation` configuration section, and the `Ignixa.Federation` `ActivitySource` and `Meter`.
- [ ] **No partial-result code path.** Search for fallbacks, `catch` blocks that return an empty list, and options that skip a shard.
- [ ] **Tests prove behaviour.** A new capability has a test that fails without it. Every test project still has at least one test.
- [ ] **The build is clean.** Warnings are errors, public API in `src/` has XML documentation, and no NuGet audit warning is suppressed.
- [ ] **Docs match the code.** The README status table claims only what exists at this commit.
