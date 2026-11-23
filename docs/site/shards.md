---
title: Register and verify shards
description: Describe physical owners, avoid ambiguous links, and distinguish registry validation from backend certification.
sidebar_position: 7
---

# Register and verify shards

**Registration answers “which owner?” Certification answers “what can this owner do correctly?”** A successfully constructed [registry][registry] establishes identity, URL ownership, profile selection and credential configuration. It does not perform a network request. A [capability admission source][source] must provide evidence separately before a proof-backed host can publish a view.

## Describe each owner

This illustrative configuration fragment describes one owner; it is **not a settings-only warm startup**. `Profile` must name a profile supplied by the composed host. Use [Configuration](https://brendankowitz.github.io/fhir-federation-service/configuration/) for the generated option reference and [binding](binding.md) for service requirements. [Registry validator][registry] · [Credential tests][auth].

```json
{
  "Federation": {
    "Shards": [
      {
        "Id": "shard-east",
        "Alias": "east",
        "BaseUrl": "https://east.example.org/fhir",
        "Profile": "MicrosoftFhirServer",
        "Auth": {
          "Type": "ManagedIdentity",
          "Resource": "https://east.example.org"
        }
      }
    ]
  }
}
```

- **Identity and order:** give every owner a unique stable ID and alias. Position in `Shards` determines the ordinal; environment overrides use indexed keys such as `Federation__Shards__0__BaseUrl`. Preserve ordering across replicas. The registry version includes ordinal, ID, alias, canonical bases and profile name, but not credentials. Rotating a credential alone does not change that version. [Registry][registry] · [Registry version tests][version].
- **Base and aliases:** use the canonical client-visible HTTPS base for emitted `fullUrl` values. Add `LinkBaseAliases` only for addresses you trust to identify that same owner. Avoid collisions or one owner's base nested under another's on the same origin. Validation errors identify the offending configuration paths. [Base URL tests][bases].
- **Outbound identity:** set the shard's token audience in `Auth:Resource`. For managed identity, `ClientId` can select a user-assigned identity; for client credentials configure `TenantId`, `ClientId` and `SecretName`, not the secret itself. A well-formed description proves neither token acquisition nor read permission. Never use the incoming bearer token as the backend credential. [Auth validation tests][auth] · [Request client contract][factory].

## Check placement before enabling passthrough

Suppose both east and west hold `Patient/42`. In an east Observation, `subject.reference = "Patient/42"` names the **east** patient. To refer to the west patient, store an absolute reference under west's canonical base or registered alias. Do not probe other shards for a relative ID or follow a foreign absolute URL. Use the emitted owner `fullUrl` or eligible owner-qualified alternate read when verifying your fixture. [Design §§4, 7][design] · [Response tests][responses].

`ChainResolution:Default` is `Distributed`. Mark a path `Passthrough` only when the references really are shard-local **and** native search for that path is certified. This is a data placement assertion, not a shortcut for a missing distributed strategy; an incorrect assertion can miss a match. [Chain options][chain] · [Passthrough endpoint tests][passthrough].

## Verify before admitting traffic

The [design's profile checks (§9.2)][design] call for matching backend software, rejection of an unknown parameter under strict handling, monotone `_lastUpdated` order and repeatable next-page redemption. Those are **requirements on evidence**, not checks performed by `ShardRegistry.TryCreate`. Confirm that the composed source can reject incomplete or contradictory evidence, and that withdrawal turns readiness unhealthy. Then verify a supported query with known, non-sensitive rows on both owners. Do not infer support for every combination from an advertised search parameter. [Source contract][source] · [Catalog tests][catalog] · [Search matrix](status.md).

[registry]: ../../src/Ignixa.Federation.Core/Shards/ShardRegistry.cs
[source]: ../../src/Ignixa.Federation.Api/Search/ICapabilityAdmissionSource.cs
[auth]: ../../test/Ignixa.Federation.Core.UnitTests/Shards/ShardRegistryAuthTests.cs
[bases]: ../../test/Ignixa.Federation.Core.UnitTests/Shards/ShardRegistryBaseUrlTests.cs
[version]: ../../test/Ignixa.Federation.Core.UnitTests/Shards/ShardRegistryVersionTests.cs
[factory]: ../../src/Ignixa.Federation.Api/Search/IShardClientFactory.cs
[design]: ../superpowers/specs/2026-11-19-fhir-federation-service-design.md
[responses]: ../../test/Ignixa.Federation.Tests.Integration/Responses/SearchsetResponseTests.cs
[chain]: ../../src/Ignixa.Federation.Core/Configuration/ChainResolutionOptions.cs
[passthrough]: ../../test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchForwardChainTests.cs
[catalog]: ../../test/Ignixa.Federation.Tests.Integration/Search/CatalogBackendHostTests.cs
