---
title: Run and adapt the .NET sample client
sidebar_position: 7
description: Exercise the client offline, interpret its collision transcript, and reuse its search and read patterns.
---

# Run and adapt the .NET sample client

The [.NET sample](https://github.com/brendankowitz/fhir-federation-service/tree/REVISION/samples/Ignixa.Federation.SampleClient)
uses the public HTTP interface, not the federation's server projects. It includes a CLI and internal types you can
copy. It is not a published client SDK or token acquisition library. Use the [generated client API reference](client-api.md)
to look up the types while adapting the examples.

## Exercise the CLI without starting a service

From the repository root, select the SDK in [global.json](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/global.json)
(11.0.100). Restore the test project while package sources are available; it references the sample:

```sh
dotnet restore test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj
```

Then build, check usage and run the scripted suite offline:

```sh
dotnet build samples/Ignixa.Federation.SampleClient -c Release --no-restore
dotnet run --project samples/Ignixa.Federation.SampleClient -c Release --no-build -- --help
dotnet test --project test/Ignixa.Federation.SampleClient.UnitTests/Ignixa.Federation.SampleClient.UnitTests.csproj -c Release --no-restore
```

Help prints the accepted arguments without issuing a search. The
[scripted handler](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/ScriptedHandler.cs)
returns synthetic responses and records requests. In the [sample transcript](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/README.md#output),
two matches share `Patient/123` but have different owners. One include appears on both pages and one outcome
warns about truncation. The CLI reports `summary: pages=2 matches=2 includes=1 outcomes=1`, then reads the first
match via the alternate link. This is not output from a public deployment.

## Run a collision search on a configured federation

Use disposable synthetic data. The following command is illustrative: replace the example base with an
authenticated, search-capable deployment holding the [oracle collision seeds](https://github.com/brendankowitz/fhir-federation-service/tree/REVISION/test/fixtures/federation-oracle/seeds/collision).
In PowerShell, enter a federation bearer token without echoing it or putting it on the command line:

```powershell
$env:FEDERATION_ACCESS_TOKEN = [Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Federation token')).Password
dotnet run --project samples/Ignixa.Federation.SampleClient -c Release --no-build -- --base https://fed.example/fhir/ --type Patient --param name=Orchid --param _count=1 --max-pages 10 --read alternate
Remove-Item Env:FEDERATION_ACCESS_TOKEN
```

Each `--param` adds one parameter. `--base` overrides `FEDERATION_BASE_URL`. The default read mode is `none`;
use it if you only need search results. With eligible proxy reads enabled, `alternate` should read the first
match through the federation. `direct` needs an explicit `--direct-shard` and uses `SHARD_ACCESS_TOKEN` if set,
not the federation token. See [read credentials](client-reads.md) before enabling it.

For the oracle case, expect two distinct owning URLs ending in `/Patient/dup`, one match per nonempty page,
and no `stopped after` message. If the page limit stopped the run, do not interpret its summary as a total.
CLI output does not contain the resource bodies; use library code to check east/Eli and west/Gus.

## Use the client in code

Inside the sample assembly, supply the trusted base and a token supplier. A supplier is called per request;
returning `null` makes an anonymous request, which is not sufficient for search on a configured host.

```csharp
using Ignixa.Federation.SampleClient;

using var client = new FederationClient(new FederationClientOptions
{
    FederationBaseUrl = new Uri("https://fed.example/fhir/"),
    FederationAccessToken = _ => ValueTask.FromResult(Environment.GetEnvironmentVariable("FEDERATION_ACCESS_TOKEN")),
});

SearchResultSet results = await SearchResultSet.FromAsync(
    client.SearchAsync("Patient", [new("name", "Orchid"), new("_count", "1")]));
foreach (FederatedEntry match in results.Matches)
{
    Console.WriteLine($"{match.FullUrl} (shard={match.SourceShard})");
}
```

`FromAsync` consumes all pages and keeps distinct matches and includes by `fullUrl`. Processing the
[page stream](client-paging.md) directly avoids collecting those resources. It does **not** guarantee constant
memory: the [client](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/FederationClient.cs)
retains followed URLs to detect paging cycles. For a fixture-specific body assertion, see
[Interpret a searchset Bundle](client-bundles.md). Handle [failures](client-errors.md) during enumeration; the
sample does not refresh credentials, follow redirects or restart an expired search on your behalf.
