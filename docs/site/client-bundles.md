---
title: Interpret a searchset Bundle
sidebar_position: 3
description: Classify matches, includes and outcome issues, and preserve resource identity across shards and pages.
---

# Interpret a searchset Bundle

A successful search returns a `Bundle` of type `searchset`. Do not deserialize `entry` as a list of search hits:
an entry can also supply a related resource or explain a limitation of this page. Start with the
[first page example](client-http.md). Its `resource.id` is `dup`, but its `fullUrl` includes the east shard's base.

## Keep three collections

| `entry.search.mode` | What belongs in the result |
| --- | --- |
| `match` | The hit's resource, absolute `fullUrl`, and optional owner alias and alternate link. The sample treats absent mode as `match`. |
| `include` | A related resource. Index it by its own `fullUrl`, separately from matches. An include may legitimately recur on later pages. |
| `outcome` | Issues from an `OperationOutcome`. Preserve severity, code, diagnostics and the source alias if present. Do not add this resource to either identity index. |

The [sample parser](../../samples/Ignixa.Federation.SampleClient/SearchPage.cs)
rejects unknown modes, a missing entry resource, or an `outcome` whose resource is not an `OperationOutcome`.
For matches and includes it requires an absolute `fullUrl`, type and id. It clones the resource JSON so you can
inspect it after the response document has been disposed.

**Choose the right key.** `https://fhir-east.example.org/Patient/dup` and
`https://fhir-west.example.org/Patient/dup` are different patients, not successive versions. The
[oracle fixture](../../test/fixtures/federation-oracle/README.md)
expects east's `name[0].given` to contain `Eli` and west's to contain `Gus`. The owner alias comes from the
**entry** extension with URL `http://ignixa.io/federation/StructureDefinition/source-shard`, not the resource
body or the hostname. Use the alias to label a result, not to manufacture a read URL.

## Aggregate across pages

Inside the sample assembly, use the two collections that
[SearchResultSet](../../samples/Ignixa.Federation.SampleClient/SearchResultSet.cs)
already maintains. Here `client` is the configured [sample client](sample-client.md):

```csharp
SearchResultSet results = await SearchResultSet.FromAsync(
    client.SearchAsync("Patient", [new("name", "Orchid"), new("_count", "1")]));

foreach (FederatedEntry patient in results.Matches)
{
    // Synthetic collision fixture: both resources have a given name.
    string? given = patient.Resource.GetProperty("name")[0].GetProperty("given")[0].GetString();
    Console.WriteLine($"{patient.FullUrl}: {given}");
}
```

For the oracle case you should see **two** different URLs and the two corresponding names. Do not assume an
arbitrary FHIR Patient has `name[0].given[0]`; that assertion is about these seeds.

For a separate worked example, the [sample transcript](../../samples/Ignixa.Federation.SampleClient/README.md#output)
contains two colliding `Patient/123` matches, a repeated `Organization/9` include and one warning issue. The
summary is `pages=2 matches=2 includes=1 outcomes=1`. Adding the per-page include counts would incorrectly give
two. The [search tests](../../test/Ignixa.Federation.SampleClient.UnitTests/SearchTests.cs)
check both collision identity and repeat handling. Next, [read the selected entry](client-reads.md) rather
than looking it up by bare ID.
