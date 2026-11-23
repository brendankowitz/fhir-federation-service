---
title: Client developer guide
sidebar_position: 1
description: Search a FHIR federation, follow opaque page links, and read resources without confusing shard identities or credentials.
---

# Client developer guide

Use the [.NET sample](https://github.com/brendankowitz/fhir-federation-service/tree/REVISION/samples/Ignixa.Federation.SampleClient)
to search, follow a continuation, and read the right patient when two shards use the same ID. You can exercise
the client offline, then try the same requests against the real API in an in-process test with synthetic shards.

## What the host serves

The merged [API host](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Program.cs)
maps these routes. Paths are relative to the federation's public base:

| Request | What to expect |
| --- | --- |
| `GET /metadata` | An R4 `CapabilityStatement` describing currently admitted search capabilities. No caller token required; `503` if no capability view is available. |
| `GET /{type}?…` | Authenticated type search; returns a JSON `searchset` Bundle. |
| `POST /{type}/_search` | The same search using a UTF-8 `application/x-www-form-urlencoded` body, not a JSON resource. The host processes URL parameters first, then body parameters. The sample CLI uses GET. |
| `GET /shards/{alias}/{type}/{id}` | Authenticated, owner-qualified read when proxy reads are enabled and the type is eligible. Follow the offered `alternate` link. |
| `GET /{type}/{id}` | `400` with an `OperationOutcome`: an unqualified ID is ambiguous. The host does not look it up. |

The [search routes](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs)
exist even without federation settings: search and qualified reads then return `503`, not `404`. The unconfigured
host's health probes can both be green. On a configured host without a current capability view, `/health/live`
remains up but `/health/ready` and capability-dependent routes return `503`. A settings-only launch is **not**
a warm two-shard demo: explicit `PrototypeCatalog` mode requires a registered capability admission source or
startup fails before listening. The real shard HTTPS transport and capability verification are separate backend
work (#19), not something this guide assumes is installed or wired. See the
[cold-host](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs)
and [catalog startup tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/CatalogBackendHostTests.cs).

## Run the client without a federation

From the repository root, use the .NET SDK selected by
[global.json](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/global.json) (11.0.100).
Restore the test project once while package sources are available; it references the sample:

```sh
dotnet restore test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj
```

After restore, these commands need no service, credentials, or network access:

```sh
dotnet build samples/Ignixa.Federation.SampleClient -c Release --no-restore
dotnet run --project samples/Ignixa.Federation.SampleClient -c Release --no-build -- --help
dotnet test --project test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj -c Release --no-restore
```

Help lists the CLI options without sending a request. The tests use a
[scripted HTTP handler](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/ScriptedHandler.cs)
that returns synthetic responses and records requests. For a two-page CLI transcript from that setup, see the
[sample README](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/README.md#output).

## Search and read a Bundle

The [host paging test](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchTests.cs)
sends `GET /Patient?name=Orchid&_count=1` through the real API with a synthetic signed caller token. Reproduce it
from the repository root (restore the integration test project once before using `--no-restore` offline):

```sh
dotnet restore test/Ignixa.Federation.Tests.Integration/Ignixa.Federation.Tests.Integration.csproj
dotnet test --project test/Ignixa.Federation.Tests.Integration/Ignixa.Federation.Tests.Integration.csproj -c Release --no-restore --filter-method "*Paging_WalksEveryPageThroughOpaqueNextLinks_InRegistryOrder"
```

Here is the test's first exchange in HTTP notation. Headers are abbreviated, JSON is formatted for readability,
and both tokens are placeholders. The in-process route is `/Patient`; the public links come from the
[fixture's configured public base](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/SearchWorld.cs).

```http
GET /Patient?name=Orchid&_count=1 HTTP/1.1
Authorization: Bearer REDACTED
```

```http
HTTP/1.1 200 OK
Content-Type: application/fhir+json; charset=utf-8

{
  "resourceType": "Bundle",
  "type": "searchset",
  "link": [
    {"relation": "self", "url": "https://fhir.example.test/federation/Patient?name=Orchid&_count=1"},
    {"relation": "next", "url": "https://fhir.example.test/federation/Patient?ct=EXAMPLE"}
  ],
  "entry": [{
    "fullUrl": "https://fhir-east.example.org/Patient/dup",
    "extension": [{"url": "http://ignixa.io/federation/StructureDefinition/source-shard", "valueString": "k7q2"}],
    "search": {"mode": "match"},
    "resource": {"resourceType": "Patient", "id": "dup", "active": true, "name": [{"family": "Orchid", "given": ["Eli"]}]}
  }]
}
```

The test walks four scripted pages: east `dup`, east `east-only`, west `dup`, west `west-only`. It checks paging,
not whether a real shard applies the name filter. There is no alternate link in this fixture. When offered,
`entry.link` navigates to a **resource**; `Bundle.link` navigates the **search**.
`entry.fullUrl` identifies the resource at its owning shard. The `source-shard` extension supplies
the shard alias; do not derive it from the hostname.

[SearchPage](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/SearchPage.cs)
classifies each entry by `search.mode`:

| Mode | How to use the entry |
| --- | --- |
| `match` | A search hit. The sample also treats an absent mode as `match`. |
| `include` | A related resource, not another hit. Keep it separately and deduplicate repeated includes by `fullUrl` across pages. |
| `outcome` | An `OperationOutcome` about the page. Surface its issues (for example, `warning/incomplete` when includes were truncated); do not count them as matches or discard the successful page. |

## Follow the next link, not the page size

For the Bundle above, the next request is `GET /federation/Patient?ct=EXAMPLE` on `fhir.example.test` **because that is the
returned URL**. `EXAMPLE` stands for an opaque server-issued value. Resolve relative links against the federation
base, check scope, and follow the link as parsed. Do not decode, log, edit, or manufacture `ct`, or append the
original filters. The sample rejects `ct` on an initial search.

`_count` is a maximum number of **matches**, not a promised page length or a total. Even a short or empty page is
not the end if it has a `next` link. Stop when `next` is absent; fail rather than loop if a link repeats.
An HTTP **410 Gone on a continuation** means the search expired: the sample stops (CLI exit `3`), without replaying
the request. To start over, use the original search parameters and a fresh result set.

An HTTP failure is different from an `outcome` entry in a successful Bundle. On an error response the sample
surfaces the status and any parsable `OperationOutcome.issue` details, not the raw response body. No FHIR body is
required for the status to be an error. Other HTTP failures, including redirects, normally exit `4`. The
[search tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/SearchTests.cs)
cover both outcome paths, repeated links, and the absence of a retry after expiration.

On the host, distinguish `400` (invalid or too costly), `415` (unsupported format), and `501` (valid search the
host cannot execute) from `502` (a shard failed), `503` (unavailable), and `504` (deadline expired).
For example, `[base]/Patient?ct=…` takes **only** the continuation; do not combine it with `_count`. HTTP
continuations are caller-bound and inline; failure to fit a next link can return `503`, not a partial success.
Writes, history, vread and `$operation` paths can fall through to an empty router `404` or `405`; don't assume
every error has a FHIR body. See the [search implementation](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs)
and [error responses](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/SearchOutcome.cs).

## Choose a read route and its credential together

The CLI optionally reads the **first match** after paging. Choose the destination before choosing a token:

| Operation | Target | Optional CLI bearer credential |
| --- | --- | --- |
| Search or next page | Configured federation base | `FEDERATION_ACCESS_TOKEN` |
| `--read alternate` | Returned `entry.link` with relation `alternate`, inside the federation base. A federation with proxy reads enabled can offer this route. | `FEDERATION_ACCESS_TOKEN` |
| `--read direct` | `entry.fullUrl`, inside a shard base explicitly listed with `--direct-shard` | `SHARD_ACCESS_TOKEN`, **never** the federation token |

The sample can run anonymously against a scripted fixture; **configured search requires a federation bearer JWT**.
A missing or untrusted token gets `401` with a Bearer challenge. A valid token whose app `roles` do not grant
the resource type gets `403`; a SMART scope in `scp` or `scope` is also refused, even with a matching role.
Neither failure sends a shard request. See the [authorization tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs).
Supply tokens through the
environment, not CLI arguments; the sample does not acquire them. Configure only trusted HTTPS bases. Before
attaching a credential, the [endpoint check](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/ScopedEndpoint.cs)
checks scheme, host, port and path boundary (`/fhirx/` is not inside `/fhir/`). The sample never follows redirects.
An out-of-scope link, unlisted direct shard, or missing requested alternate link ends the CLI run with exit `5`
without sending that request. The [credential tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/CredentialConfinementTests.cs)
check which endpoint actually received which credential. Treat URLs, patient identifiers and server diagnostics as
sensitive even when the CLI omits tokens and bodies.

## Walk through the collision with the CLI or library

The [oracle's `collision.tagged-union` case](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/fixtures/federation-oracle/README.md)
specifies what to check on a search-capable deployment with the synthetic collision fixtures. This is the **shape**
of the CLI invocation; `fed.example` is a placeholder, not an offline demo:

```sh
dotnet run --project samples/Ignixa.Federation.SampleClient -c Release --no-build -- --base https://fed.example/fhir/ --type Patient --param name=Orchid --param _count=1 --max-pages 10 --read alternate
```

Expect two distinct matches across pages. In the fixture, `https://fhir-east.example.org/Patient/dup` is Eli
and `https://fhir-west.example.org/Patient/dup` is Gus. Keying by `Patient/dup` loses one patient. Check the two
owning URLs and distinct aliases, not just a count of two; a duplicate page could produce the same count.
If the CLI reports that it stopped at `--max-pages`, the traversal is incomplete. The CLI prints identifiers
and counts, not bodies; verifying Eli versus Gus requires inspecting the returned resources. For the CLI's tested
output and exit codes, see [CliAppTests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/CliAppTests.cs).

To use the same logic in an application, copy the pattern from
[FederationClient](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/FederationClient.cs).
Its types are internal to the sample, not a published SDK. This example belongs inside the sample assembly:

```csharp
using Ignixa.Federation.SampleClient;

using var client = new FederationClient(new FederationClientOptions
{
    FederationBaseUrl = new Uri("https://fed.example/fhir/"),
    FederationAccessToken = _ => ValueTask.FromResult<string?>(
        Environment.GetEnvironmentVariable("FEDERATION_ACCESS_TOKEN")),
});

SearchResultSet results = await SearchResultSet.FromAsync(
    client.SearchAsync("Patient", [new("name", "Orchid"), new("_count", "1")]));

foreach (FederatedEntry match in results.Matches)
{
    Console.WriteLine($"{match.FullUrl} (shard={match.SourceShard})");
}
```

`SearchAsync` yields pages during enumeration. Process them with `await foreach` if you do not need to retain
them all; [SearchResultSet](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/SearchResultSet.cs)
instead collects distinct matches and distinct includes by `fullUrl`, and retains the outcome issues. For a
follow-up read, call `ReadViaFederationAsync(match)` when `match.Alternate` exists, or configure `DirectReadShards`
with the shard base and its token supplier before calling `ReadDirectAsync(match)`. Dispose the returned `JsonDocument`.
