# Contributing

Ignixa Federation is pre-alpha. Read the [design spec](docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md) first. It defines what the service does and does not do.

## Prerequisites

* The .NET SDK version pinned in [global.json](global.json), currently 11.0.100.
* Git.
* Docker, only if you work on `deploy/`.

## Build and test

[AGENTS.md](AGENTS.md) has the exact commands. The short form, from the repository root:

```sh
dotnet restore Ignixa.Federation.slnx
dotnet build Ignixa.Federation.slnx -c Release
dotnet test --solution Ignixa.Federation.slnx -c Release
```

Warnings are errors, so a clean build is part of the change.

## Branch and pull request flow

1. Open or pick an issue.
2. Branch from `main`. Keep the branch to one change that a reviewer can verify on its own.
3. Run the build and the tests before you push.
4. Open a pull request against `main`. CI must pass.
5. Fill in the pull request template. Its headings are `Why`, `What changed`, `Scope`, `Tradeoffs`, `Blast Radius`, and `Verification`.
6. Address review comments with new commits. Reviewers use [REVIEW.md](REVIEW.md).

## Commit style

Use `type(scope): subject`, for example `fix(api): return 503 when no shard is ready`. Write the subject in the imperative mood and keep it under about 72 characters.

## Design changes

The spec is the authority. If your change alters behaviour that the spec describes, edit the spec in the same pull request and say so in the `Why` section. Do not add behaviour that the spec lists as a non-goal. Do not weaken an invariant in REVIEW.md without an owner decision recorded in the spec.

## Security

Do not report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).
