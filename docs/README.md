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

From the repository root, with the pinned .NET SDK, Node.js 22.23.5 with npm, PowerShell 7.3+ and Python 3.9+:

```sh
pwsh -File tools/build-docs.ps1
npm --prefix docs/site run preview
```

Open `http://localhost:4321/fhir-federation-service/`. The first command restores locked npm dependencies, generates the C# references, stages Markdown, builds `docs/site/dist/` (including the Pagefind search index), and validates links and migrated bookmarks. Use `-Dotnet <path>` to select another `dotnet` executable. Do not commit generated files.

Edit published topics in `docs/site/*.md`, the [client landing page](guides/client-developer-guide.md) in `docs/guides/`, or the [home page](site/src/content/docs/index.mdx) in the Starlight collection. Register new topics in [the sidebar](site/astro.config.mjs). Link between authored files using checkout-relative paths; staging converts them to canonical `/slug/` URLs. For generated reference pages, use the full published URL. Link to source, tests and numbered design sections by relative path; staging pins repository evidence to the exact checkout SHA. When changing host behavior, update the [support matrix](site/status.md) in the same PR: a passing Core test alone is not evidence of a reachable HTTP feature.

Configuration defaults and shard contracts come from Core; the sample API reference comes from the SampleClient assembly and its XML comments, not a supported SDK package. Edit those sources, not the ignored `src/content/docs/generated/` files. Old `.html` bookmarks redirect to the canonical routes and retain their fragments; `tools/legacy-docs-urls.json` records the public URL contract.

PRs build but never deploy. Main publishes the validated artifact to the [project Pages site](https://brendankowitz.github.io/fhir-federation-service/) when the repository administrator has selected **Settings → Pages → Build and deployment → Source: GitHub Actions**. After enabling this, run the Documentation workflow on `main` if it has already built.
