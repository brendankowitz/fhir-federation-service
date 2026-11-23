---
title: Read a match from the right owner
sidebar_position: 5
description: Choose a proxied or direct read, validate its URL, and keep federation and shard credentials separate.
---

# Read a match from the right owner

Once you select an entry, keep the entry itself. A read reconstructed from just `resourceType` and `id` loses
the owner. In the collision corpus, `GET [base]/Patient/dup` could mean Eli or Gus. The
[unqualified read route](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Reads/ProxiedReads.cs)
returns `400` with an `OperationOutcome` explaining the ambiguity; it never searches for an owner.

## Choose one of two destinations

| Use this when… | Target from the entry | Credential |
| --- | --- | --- |
| You can reach the federation but not the owner, and an alternate is offered | `entry.link` with `relation: "alternate"` | Federation token for the configured federation base |
| You have independent access to the owner and have opted into that shard | `entry.fullUrl` | That shard's credential; never the federation token |

For example, these are **alternatives**, not a redirect sequence. For east's patient, a deployment that offers
proxy reads could issue the first link. A client configured to read east directly could follow the second:

```http
GET /federation/shards/k7q2/Patient/dup HTTP/1.1
Host: fhir.example.test
Authorization: Bearer FEDERATION_TOKEN_PLACEHOLDER
```

```http
GET /Patient/dup HTTP/1.1
Host: fhir-east.example.org
Authorization: Bearer EAST_TOKEN_PLACEHOLDER
```

Expect a Patient resource, not another Bundle. In the fixture, check for east's `Eli`, not merely `id: "dup"`.
An absent alternate is normal if proxy reads are disabled or the resource is ineligible. Do not invent an
alternate or silently try a direct read: that is a different authorization decision. A qualified read returning
`410` means the resource was deleted, **not** that a search page expired. See the
[proxied read tests](https://github.com/brendankowitz/fhir-federation-service/tree/REVISION/test/Ignixa.Federation.Tests.Integration/Reads).

## Constrain the URL before attaching a token

For both methods the [sample endpoint](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/ScopedEndpoint.cs)
checks HTTPS, host, port and path boundaries: allowing `https://shard.example/fhir/` does not allow
`https://shard.example/fhirx/Patient/dup`. Configure nonoverlapping bases. A matching alias or familiar-looking
hostname in the Bundle is not permission to send a credential. Redirects are not followed, even if the first
URL was trusted. The [credential tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/CredentialConfinementTests.cs)
inspect which handler received each request and bearer header, and check that rejected links sent nothing.

In the [sample CLI](sample-client.md), `--read alternate` follows the first match's offered link. To choose the
other route instead, use `--read direct --direct-shard https://fhir-east.example.org/`. List each trusted shard
explicitly. The CLI uses `SHARD_ACCESS_TOKEN` for listed shards; if owners require different tokens, use a
per-shard supplier in the library:

```csharp
using var client = new FederationClient(new FederationClientOptions
{
    FederationBaseUrl = new Uri("https://fhir.example.test/federation/"),
    FederationAccessToken = _ => ValueTask.FromResult(Environment.GetEnvironmentVariable("FEDERATION_ACCESS_TOKEN")),
    DirectReadShards = [new ShardReadOptions
    {
        BaseUrl = new Uri("https://fhir-east.example.org/"),
        AccessToken = _ => ValueTask.FromResult(Environment.GetEnvironmentVariable("EAST_ACCESS_TOKEN")),
    }],
});
// eastMatch is the selected FederatedEntry from the search.
using System.Text.Json.JsonDocument patient = await client.ReadDirectAsync(eastMatch);
```

For an explicitly selected proxied read, use `ReadViaFederationAsync(eastMatch)` instead after checking that
`eastMatch.Alternate` exists. Dispose the returned document in either case. The sample has no automatic fallback.
