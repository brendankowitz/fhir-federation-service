---
title: Send a FHIR search
sidebar_position: 2
description: Discover supported searches, send authenticated GET and form POST requests, and reproduce a synthetic first page.
---

# Send a FHIR search

Start with the federation's public HTTPS base, not a shard address. `GET [base]/metadata` requires no caller token.
When ready, it returns a JSON R4 `CapabilityStatement` describing the admitted capabilities; it does **not** grant
you access to those resource types. With no published capability view it returns `503`.
See the [metadata endpoint](../../src/Ignixa.Federation.Api/Metadata/MetadataEndpoints.cs).

## Make the first request

Authenticate with a bearer JWT for the federation. On a ready host, a missing or untrusted token gets a `401`
Bearer challenge before any shard call. A valid token needs a configured app role granting the requested type.
A SMART scope in `scp` or `scope` is refused with `403`, even alongside a granted role. See the
[authorization tests](../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchAuthorizationTests.cs).

This is the first exchange from `Paging_WalksEveryPageThroughOpaqueNextLinks_InRegistryOrder` in the
[host tests](../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchTests.cs),
shown in HTTP notation. The test sends the route relative to its in-process host. Headers are abbreviated,
tokens replaced, and JSON formatted. The response links use the fixture's **configured public base**.

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
    {"relation":"self","url":"https://fhir.example.test/federation/Patient?name=Orchid&_count=1"},
    {"relation":"next","url":"https://fhir.example.test/federation/Patient?ct=EXAMPLE"}
  ],
  "entry": [{
    "extension":[{"url":"http://ignixa.io/federation/StructureDefinition/source-shard","valueString":"k7q2"}],
    "fullUrl":"https://fhir-east.example.org/Patient/dup",
    "resource":{"resourceType":"Patient","id":"dup","active":true,"name":[{"family":"Orchid","given":["Eli"]}]},
    "search":{"mode":"match"}
  }]
}
```

**Reproduce it:** from the repository root, restore once, then run the test offline. No external identity provider,
Redis instance, or shard is needed for this test.

```sh
dotnet restore test/Ignixa.Federation.Tests.Integration/Ignixa.Federation.Tests.Integration.csproj
dotnet test --project test/Ignixa.Federation.Tests.Integration/Ignixa.Federation.Tests.Integration.csproj -c Release --no-restore --filter-method "*Paging_WalksEveryPageThroughOpaqueNextLinks_InRegistryOrder"
```

The test follows four pages: east `dup`, east `east-only`, west `dup`, west `west-only`. The scripted transport
supplies the pages; it is not testing the shard's name filter. Inspect the [Bundle](client-bundles.md) before
following [the next link](client-paging.md).

## Use form POST when the initial parameters belong in a body

The API also accepts `POST [base]/{type}/_search`. This example uses the form tested by the same host suite:

```http
POST /Patient/_search?name=Orchid HTTP/1.1
Authorization: Bearer REDACTED
Content-Type: application/x-www-form-urlencoded; charset=utf-8

family=Orchid&name=Or%20chid
```

The host reads URL parameters first and then body parameters. Both `name` occurrences survive; a dictionary
with one slot per name would silently lose a predicate. The `self` link describes the combined search.
This POST is a **search**, not a write. Sending a Patient JSON body instead of form data gets `415`.
Follow subsequent pages with GET, even after POST.

## If the first search returns 503

The [routes](../../src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs)
are mapped even on an unconfigured host. Search returns `503`, not `404`, and both health probes can still be green.
A configured host without a current view stays live but not ready. Explicit `PrototypeCatalog` mode without
its required admission source fails before listening. The merged API (#17) and its in-process test bindings are
not a settings-only installation of the separate shard backend work (#19). Compare the
[cold-host tests](../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs)
and [catalog startup tests](../../test/Ignixa.Federation.Tests.Integration/Search/CatalogBackendHostTests.cs)
before diagnosing a `503` as a bad query.
