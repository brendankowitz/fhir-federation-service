# Ignixa.Federation.SampleClient

A small .NET 11 console sample for developers writing a client of the FHIR federation service. It is a worked
example to copy from, not an SDK: no third-party packages, no retries, no token acquisition.

The wire contract is `docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md`
(§4 identity, §5 request surface, §7.4 continuation tokens, §8 failures, §10 security, §11 response shaping).

## What it demonstrates

- **Search and paging.** `GET [base]/[type]?…`, then each `Bundle.link[relation=next]` followed as the federation gave it (as parsed by `System.Uri`, which can normalise
  unreserved escapes such as `%7e` to `~`).
  The link is opaque: the client never decodes, edits or rebuilds it or prints its `ct` value; the CLI only looks at it to hide echoes.
- **match / include / outcome.** `search.mode` separates matches from included resources and from `outcome`
  entries (for example "included resources truncated", `incomplete`). Outcomes are data, not errors.
- **Identity is `fullUrl`.** Resource ids are only unique per shard, so `Patient/123` can exist on
  `shard-east.example` and `shard-west.example` at once. The sample keys matches and includes by `fullUrl`; those are two
  entries. An include repeated on a later page is counted once.
- **410 means restart, not replay.** An expired continuation ends the run (exit code 3). The client does not retry
  or restart; start a new search from the original query.
- **Follow-up read, two ways** (the CLI reads the first match):
  - `--read alternate`: `entry.link[relation=alternate]`, the federation's proxied route
    `shards/{alias}/{type}/{id}`, using the **federation** credential.
  - `--read direct`: `GET entry.fullUrl` on the shard itself. Opt-in: the shard must be listed with
    `--direct-shard`. It uses the client's own **shard** credential and never the federation token.
- **Credential confinement.** A token is attached only after the target is checked to be inside its configured HTTPS
  base (same host and port, path on a segment boundary: `/fhir/` allows `/fhir/Patient`, not `/fhirx/Patient`). A
  link that points elsewhere is rejected before any request is sent (exit code 5).
- **Redirects are never followed.** Any 3xx is an error. A handler that auto-redirects is refused.

The library is `FederationClient`; `CliApp` is the command line on top of it.

## Build and run

Requires the .NET SDK 11. From the repository root:

```powershell
dotnet build samples/Ignixa.Federation.SampleClient
dotnet run --project samples/Ignixa.Federation.SampleClient -- --help
```

Search a federation (the host below is an example):

```powershell
$env:FEDERATION_ACCESS_TOKEN = [Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Federation token')).Password   # optional; omit for anonymous
dotnet run --project samples/Ignixa.Federation.SampleClient -- `
    --base https://fed.example/fhir/ --type Patient --param name=smith --max-pages 3
```

Read the first match through the federation, or directly from its shard:

```powershell
dotnet run --project samples/Ignixa.Federation.SampleClient -- `
    --base https://fed.example/fhir/ --type Patient --param name=smith --read alternate

$env:SHARD_ACCESS_TOKEN = [Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Shard token')).Password   # optional; used only for --direct-shard shards
dotnet run --project samples/Ignixa.Federation.SampleClient -- `
    --base https://fed.example/fhir/ --type Patient --param name=smith `
    --read direct --direct-shard https://shard-east.example/fhir/
```

### Options

| Option | Meaning |
| --- | --- |
| `--base <url>` | Federation base URL, absolute `https`. Or set `FEDERATION_BASE_URL`. |
| `--type <ResourceType>` | Resource type to search. |
| `--param name=value` | Search parameter; repeatable. `ct` is not accepted: only the federation issues it. |
| `--max-pages <n>` | Stop after n pages (default 5). If more pages exist, the CLI says so. |
| `--read none\|alternate\|direct` | Also read the first match (default `none`). |
| `--direct-shard <url>` | A shard base URL that may be read directly; repeatable. |
| `-h`, `--help` | Usage. |

### Environment variables

| Variable | Use |
| --- | --- |
| `FEDERATION_BASE_URL` | Default for `--base`. |
| `FEDERATION_ACCESS_TOKEN` | Optional bearer token, sent only to the federation base. |
| `SHARD_ACCESS_TOKEN` | Optional bearer token, sent only to `--direct-shard` shards. |

Credentials are never accepted as arguments (an option such as `--token` is a usage error), so they stay out of
process listings and command lines. The `Read-Host -AsSecureString` form above (it works in Windows PowerShell 5.1 and
PowerShell 7) also keeps them off the screen and out of the typed command line; a token is trimmed, and one containing
a line break is refused (exit code 2). The CLI never prints tokens, `ct` values, `next` links or query strings, or
resource bodies. It also blanks out echoes of a followed `ct` in server diagnostics, whether whole, encoded or
decoded, or cut off after at least 8 characters.

### Output

This is the real output of a two-page search with `--read alternate`, run against a scripted handler (synthetic data;
no network). Page 2 repeats the `Organization/9` include, which the summary counts once, and carries an `outcome`
entry:

```
page 1: matches=1 includes=1 outcomes=0
match  https://shard-east.example/fhir/Patient/123  (shard=k7q2)
include  https://shard-east.example/fhir/Organization/9  (shard=k7q2)
page 2: matches=1 includes=1 outcomes=1
match  https://shard-west.example/fhir/Patient/123  (shard=p9xm)
include  https://shard-east.example/fhir/Organization/9  (shard=k7q2)
outcome warning/incomplete: included resources truncated (shard=p9xm)
summary: pages=2 matches=2 includes=1 outcomes=1
Patient/123 read via alternate from https://fed.example/fhir/shards/k7q2/Patient/123
```

The summary counts distinct matches and includes by `fullUrl`.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 2 | Usage error (including a non-HTTPS base URL, a credential passed as an argument, or a token with a line break). |
| 3 | Continuation expired (HTTP 410). Restart the search from the original query. |
| 4 | Federation or HTTP error: a non-2xx status or redirect (the status and parsed OperationOutcome issues are shown, not the body), a timeout, a response that could not be read, or one that is not a valid FHIR response. |
| 5 | A link was outside the allowed scope and was not followed, including `--read direct` for a shard not given with `--direct-shard`. Also `--read alternate` when the first match has no alternate link (the federation may have proxied reads disabled); no request is sent. |

## Tests

```powershell
dotnet test --project test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj -c Release
```

`dotnet test --solution Ignixa.Federation.slnx -c Release` runs it too. It is an xunit.v3 project on
Microsoft.Testing.Platform, like the other test projects.

### Public-wire unit tests (this repository)

These drive `FederationClient` and `CliApp` against a scripted `HttpMessageHandler` that replays canned responses and
records every request (URI and `Authorization` header). The data is synthetic (`fed.example`, `shard-east.example`,
`shard-west.example`, fake tokens). There is no network and no running service.

They show that, for the responses the spec describes, the client:

- treats `fullUrl` as identity (colliding `Patient/123` stay two entries) and de-duplicates repeated includes;
- follows `next` as given (as parsed by `System.Uri`), stops when it is absent, and refuses a repeated link;
- surfaces `outcome` entries as data and turns HTTP failures into exceptions without the body, `ct` or tokens;
- sends nothing after a 410, and never follows a redirect;
- keeps each token on its own endpoint and rejects out-of-scope links before sending;
- gives the CLI the documented output and exit codes, with no token or `ct` on stdout or stderr.

### Live-service validation (not covered here)

Checking the sample against a running federation and real shards (actual `next` and 410 behavior, proxied reads,
shard authorization) needs a deployed service and is **not** part of these tests. It comes later. A passing unit test
run says the client follows the contract as written; it does not show that a particular deployment does.
