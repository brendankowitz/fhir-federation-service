# Client developer guide: build a client against the federation

This guide is for a developer writing a client of the FHIR federation service. It uses the merged sample,
[`Ignixa.Federation.SampleClient`](../../samples/Ignixa.Federation.SampleClient/), as the worked example: what to run,
what to read in the code, and what the federation's wire contract means for your client. The sample's
[README](../../samples/Ignixa.Federation.SampleClient/README.md) owns the option, environment variable and exit-code
tables; this guide links to them and adds the reasons and the lessons.

The wire contract is the [design spec](../superpowers/specs/2026-11-19-fhir-federation-service-design.md). Where this
guide and the spec differ, the spec wins.

## Current limitations

Read this before you run anything.

- **Federated search is not served yet.** The merged API host (`src/Ignixa.Federation.Api/Program.cs`) maps only
  `/health/live` and `/health/ready`. A search request to it returns 404.
- **The end-to-end journey does not work today.** Against the merged host the sample exits with code 4 (a non-2xx
  response), and the acceptance driver in Lesson 3 reports `verdict: FAIL` with a reason that names exit 4. That is the
  expected, honest result until the search routes land. Nothing in this guide shows output from a live federation.
- **Only the client side is verified today.** The sample's unit tests drive it against a scripted `HttpMessageHandler`
  with synthetic data. They show the client follows the contract as written, not that a deployment does.
- **Chains, includes and `_revinclude` are not covered here.** The collision walkthrough in Lesson 3 uses a plain
  search with no includes. A later task will capture a live success against a public federation and exercise joins
  (chains) and includes once the real routes land. This guide will be extended then.

Output blocks below are labelled either **real** (a command that was run in this repository) or **expected shape**
(synthetic, the form the driver's unit tests use).

## Lesson 1: build, run and read the sample (no service needed)

The repository pins .NET SDK 11.0.100 in `global.json`. Run the `dotnet` that has it installed (see
[AGENTS.md](../../AGENTS.md)). Run every command from the repository root.

```powershell
dotnet build samples/Ignixa.Federation.SampleClient -c Release
dotnet run --project samples/Ignixa.Federation.SampleClient -c Release -- --help
dotnet test --project test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj -c Release
```

Real output, tail of the build and of the test run (the `--help` text is the `Usage` constant in `CliApp.cs`; the
[README's options table](../../samples/Ignixa.Federation.SampleClient/README.md#options) lists the same options):

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```text
Test run summary: Passed!
  total: 121
  failed: 0
  succeeded: 121
  skipped: 0
```

The unit-test count changes as tests are added; treat it as a snapshot.

### Code tour

The types are `internal`. The sample is an example to copy from, not an SDK package, and it references no `src/`
project: it uses only the public HTTP wire contract.

| Read | To learn |
|---|---|
| `FederationClient.cs` | Search (`SearchAsync`), paging (`PagesAsync` follows each `next` link as given, stops when there is none, refuses a repeated link), and the two follow-up reads (`ReadViaFederationAsync`, `ReadDirectAsync`). |
| `ScopedEndpoint.cs` | Credential confinement: a token is attached only after the target is checked to be inside the endpoint's HTTPS base. Redirects are never followed. |
| `SearchPage.cs` | Parsing a searchset Bundle: `search.mode` (match, include, outcome), the `source-shard` extension, the `alternate` link and the `next` link. |
| `FederatedEntry.cs`, `SearchResultSet.cs` | Identity is `fullUrl`. Equality and the de-duplication sets key on it, never on type and id. |
| `SearchExpiredException.cs`, `LinkRejectedException.cs`, `FederationRequestException.cs` | The failure types. `CliApp.cs` maps them to exit codes: 410 on a continuation to 3, a rejected link to 5, other HTTP or format failures to 4. |
| `CliApp.cs`, `CliOptions.cs` | The command line: output lines, exit codes, and why credentials are environment variables only. |

### Using the library from code

Because the types are `internal`, this compiles only inside the sample assembly (or one that it exposes its internals
to). It is the shape to copy, not a published API. Token suppliers are called once per request; returning `null` sends
the request anonymously.

```csharp
using Ignixa.Federation.SampleClient;

using var client = new FederationClient(new FederationClientOptions
{
    FederationBaseUrl = new Uri("https://fed.example/fhir/"),
    FederationAccessToken = _ => ValueTask.FromResult<string?>(
        Environment.GetEnvironmentVariable("FEDERATION_ACCESS_TOKEN")),
});

var results = await SearchResultSet.FromAsync(
    client.SearchAsync("Patient", [new("name", "Orchid"), new("_count", "1")]));

foreach (var match in results.Matches)
{
    Console.WriteLine($"{match.FullUrl} (shard={match.SourceShard})");
}

if (results.Matches is [{ Alternate: not null } first, ..])
{
    using var resource = await client.ReadViaFederationAsync(first);
}
```

For a direct read, list the shard in `FederationClientOptions.DirectReadShards` (a `ShardReadOptions` with its own
`BaseUrl` and `AccessToken`) and call `ReadDirectAsync`. `SearchAsync` rejects a `ct` parameter: only the federation
issues continuation tokens.

## Lesson 2: the concepts behind the collision walkthrough

Each concept ties a spec section to where the sample handles it.

- **Identity is `fullUrl`, not type and id (spec §4).** A resource id is unique only within its shard, so two shards can
  each hold a `Patient/dup`. The federation reports each by its owning `fullUrl` (`<shard base>Patient/dup`), so those
  are two entries. Keep, compare and de-duplicate by `fullUrl`. The sample does this in `FederatedEntry` and
  `SearchResultSet`.
- **The shard alias comes from the source-shard extension (spec §4.2, §11).** Each entry carries
  `extension[source-shard]` with the shard's `Alias`, and the sample prints it as `(shard=<alias>)`. The alias is a
  short, stable, URL-safe name. It also appears in the proxied read route. Treat it as an opaque label.
- **`_count` is a per-page maximum, and pages follow shard-ordinal order (spec §7.1).** With no sort, results from
  shard k are not emitted before the shards before it are exhausted, so a small `_count` walks the shards in order. A
  page can hold fewer matches than `_count`, and a trailing empty page is possible. Do not infer the total from the
  page size, and stop when there is no `next` link.
- **Follow `next` as given, and treat it as opaque (spec §7.4).** The token inside it is an encrypted snapshot. The
  client never decodes, edits, rebuilds or prints it, and never supplies `ct` itself. The sample's `PagesAsync` follows
  the parsed link and stops on a repeat.
- **410 means restart from the original query (spec §7.4, exit 3).** A continuation expires (15 minutes sliding, 60
  absolute per the spec's current choice). The sample throws `SearchExpiredException` and does not replay or retry.
  Your client should start a new search from the query it began with.
- **Outcomes are data, not errors (spec §11).** An entry with `search.mode=outcome` carries an OperationOutcome, for
  example a note that included resources were truncated. The sample surfaces it as an `outcome` line and counts it. A
  real failure is a non-2xx response (spec §8): the federation never returns a Bundle with a needed shard silently
  missing.
- **Two ways to read a resource afterwards (spec §5.1, §10.2).**
  - Alternate read (`--read alternate`): follow `entry.link[relation=alternate]`, the proxied route
    `shards/{alias}/{type}/{id}`, with the **federation** credential. It exists only when the federation has
    `ProxyReads` on. If the entry has no alternate link, the sample exits 5 without sending a request.
  - Direct read (`--read direct`): `GET entry.fullUrl` on the shard, with your own **shard** credential. It is opt-in:
    the shard must be listed with `--direct-shard`. The federation token is never sent to a shard.
- **Redirects are never followed.** The sample applies to itself the rule the spec sets for the federation's backend
  calls (§8, §10.2). Any 3xx is an error, and a handler that auto-redirects is refused.
- **An out-of-scope link is refused (exit 5).** Again the sample applies to itself the rule the spec sets for the
  federation's backend calls (§10.2). A link whose scheme, host, port or path is outside the
  endpoint that would carry the credential is rejected before any request is sent. This is what stops a hostile or
  mistaken link from collecting a token.

## Lesson 3: the live acceptance check (two-shard collision walkthrough)

This lesson runs the sample against a federation you operate and checks the result against the committed collision
case `collision.tagged-union` in the [federation oracle](../../test/fixtures/federation-oracle/README.md). **Today it
ends in `verdict: FAIL`** because search is not served yet (see [Current limitations](#current-limitations)). The
tooling is read-only: it only builds and runs the sample, which only issues GET requests.

### Credentials

Tokens are optional: leave them unset for an anonymous fixture. They are never passed as arguments. The sample reads
them from the environment, entered without echo as in the sample README:

```powershell
$env:FEDERATION_ACCESS_TOKEN = [Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Federation token')).Password
$env:SHARD_ACCESS_TOKEN = [Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Shard token')).Password   # only for --read direct
```

The driver never reads, sets or prints them: it inherits the environment the sample reads. Clear them when you finish:

```powershell
Remove-Item Env:FEDERATION_ACCESS_TOKEN, Env:SHARD_ACCESS_TOKEN -ErrorAction SilentlyContinue
```

### Fixture requirements

Use only synthetic data in disposable shards you own. Never point this at real patient data or at a shared
environment.

- Two fresh, disposable HTTPS FHIR R4 shards, loaded from
  `test/fixtures/federation-oracle/seeds/collision/east.json` and `west.json` as the oracle's
  [harness contract](../../test/fixtures/federation-oracle/README.md#harness-contract) describes (Load, step 1): PUT with
  ids preserved, all Patients first, and the reserved `https://fhir-{east,west}.example.org/` bases replaced with the
  real shard bases. This guide does not script those writes; the loading is yours to do.
- Both shards registered in the federation's `Federation:Shards` (spec §4.2) with their real `BaseUrl` and distinct
  `Alias` values, and `ProxyReads` on for the alternate read.
- The federation reachable over HTTPS with a certificate the machine trusts.

### What the walkthrough asks

`Patient?name=Orchid&_count=1` should return both `<east base>Patient/dup` and `<west base>Patient/dup`, one match per
page, and then stop (oracle harness, Pages, step 5).

### The sample command it runs

The driver is a thin wrapper over the sample. It builds the sample, then runs the built sample with the equivalent of
this command (default alternate read, default `--max-pages`):

```powershell
dotnet run --project samples/Ignixa.Federation.SampleClient -c Release -- --base <federation base> --type Patient --param name=Orchid --param _count=1 --max-pages 10 --read alternate
```

With `--read direct` it adds the two `--direct-shard` options, one per shard base. The driver runs the built assembly
directly (`dotnet <path to the built sample dll> ...`) rather than through `dotnet run`, so that a timeout can stop a
single process.

### The driver

```powershell
python -B docs/guides/client-acceptance/collision_walkthrough.py `
    --base https://fed.example/fhir/ `
    --shard-base https://shard-east.example/fhir/ --shard-base https://shard-west.example/fhir/ `
    --read alternate
```

| Option | Meaning |
|---|---|
| `--base <url>` | The federation base URL. |
| `--shard-base <url>` | A shard base URL; give it twice, once per shard. |
| `--read alternate\|direct` | Default `alternate`. `direct` passes both shard bases to the sample as `--direct-shard` and uses `SHARD_ACCESS_TOKEN`. |
| `--max-pages N` | Page limit passed to the sample. Default 10, minimum 3. |
| `--dotnet PATH` | The `dotnet` to use. Default `dotnet`. |

It builds the sample in Release, runs it, and prints the sample's own stdout and stderr (already free of tokens, `ct`
values and resource bodies). It then prints `verdict: PASS` or `verdict: FAIL`, with one `- <reason>` line per failed
check. Exit code: 0 for PASS, 1 for FAIL, 2 for a usage error.

The sample's output for a passing run has this **expected shape** (synthetic; aliases and bases are placeholders, and
the shard order follows the federation's shard ordinals):

```text
page 1: matches=1 includes=0 outcomes=0
match  <first shard base>Patient/dup  (shard=<alias>)
page 2: matches=1 includes=0 outcomes=0
match  <second shard base>Patient/dup  (shard=<other alias>)
summary: pages=2 matches=2 includes=0 outcomes=0
Patient/dup read via alternate from <federation base>shards/<alias>/Patient/dup
```

For a labelled scripted-handler transcript of a real run, see the
[README output section](../../samples/Ignixa.Federation.SampleClient/README.md#output).

### What the driver checks

- The sample exits 0.
- Every output line matches the sample's line grammar.
- Each page header agrees with the lines under it.
- At most one match per page (an empty trailing page is allowed).
- The run terminates: no `stopped after` line within `--max-pages`.
- No page carries an include or outcome line, and the summary agrees with the pages, with matches=2, includes=0 and
  outcomes=0.
- The matches are exactly the two expected `fullUrl`s, with no repeat across pages. URL comparison canonicalises scheme
  and host case and the default port 443 only.
- Every match has a shard alias, and the two aliases differ.
- Exactly one read line, in the requested mode. For `alternate`, the URL is the federation base plus
  `shards/<first match's alias>/Patient/dup`. For `direct`, it is the first match's `fullUrl`.

### What the driver does not check

- Resource bodies. The oracle's harness (Collision bodies, step 8) requires the east `Patient/dup` to carry the given
  name `Eli` and the west one `Gus`. The sample prints no bodies on purpose, so the driver cannot see them.
- Includes, chains and the other oracle cases.
- HTTP-level details beyond what the sample reports.

### What happens today

Against the merged API host the sample gets 404 for the search and exits 4. The driver then prints `verdict: FAIL`
with a reason that names exit 4. That is the correct result until search lands. A `verdict: PASS` here would need a
federation that serves search.

### The driver's own tests

The driver has unit tests that use synthetic sample output only and need no network:

```powershell
python -B docs/guides/client-acceptance/test_collision_walkthrough.py
```

CI does not run them today.

## Next steps

- Read the sample's [README](../../samples/Ignixa.Federation.SampleClient/README.md), then `FederationClient.cs` and
  `ScopedEndpoint.cs`.
- Read the spec sections cited above in the
  [design spec](../superpowers/specs/2026-11-19-fhir-federation-service-design.md): §4 identity, §5 request surface,
  §7.1 emission order, §7.4 continuation tokens, §8 failures, §10 security, §11 response shaping.
- Read the [oracle README](../../test/fixtures/federation-oracle/README.md) for the cases a federation must satisfy,
  including the chain and include cases this guide does not yet exercise.
- A later task will capture a live success and add chains and includes once the real routes land.
