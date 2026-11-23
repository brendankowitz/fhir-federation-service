# Documentation

| Section | What it holds |
|---|---|
| [Architecture](architecture/README.md) | The components and the request flow. |
| [Guides](guides/README.md) | Operator and client instructions. |
| [vNext](vnext/README.md) | Work after v1: import and export, writes, shard balancing. |
| [Design spec](superpowers/specs/2026-11-19-fhir-federation-service-design.md) | The authority for behaviour. |
| [Deployment](../deploy/README.md) | The Dockerfile and the compose stack. |

Other entry points: the [README](../README.md) has the capability status table, [AGENTS.md](../AGENTS.md) has the build commands, and [REVIEW.md](../REVIEW.md) has the review criteria.

## Build and maintain the documentation site

With the pinned .NET SDK, PowerShell 7.3+ and Python 3 installed, run `pwsh -File tools/build-docs.ps1` from the repository root. Use `-Dotnet <path>` to select a specific `dotnet` executable. Open `artifacts/docs/_site/index.html` through a static web server to preview. Do not commit generated files.

Edit the site navigation in `docs/site/toc.yml` and its explicit page list in `tools/build-docs.ps1`. Cite behavioral claims with links to code, tests or numbered design sections. Use `/blob/REVISION/` for tracked files and `/tree/REVISION/` for directories; the build replaces `REVISION` with the exact checkout SHA. When changing host behavior, update the [support matrix](site/status.md) in the same PR: a passing Core test alone is not evidence of a reachable HTTP feature. Configuration defaults and selected shard contracts are generated from Core; update their source comments rather than copying the generated tables.

PRs build but never deploy. Main publishes the validated artifact to the [project Pages site](https://brendankowitz.github.io/fhir-federation-service/) when the repository administrator has selected **Settings → Pages → Build and deployment → Source: GitHub Actions**. After enabling this, run the Documentation workflow on `main` if it has already built.
