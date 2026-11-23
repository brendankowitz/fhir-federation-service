# Ignixa Federation

Two shards can both return `Patient/42`. To use federated search, you need to know not just *which* patient matched, but *which server owns it*. The [design, §5](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/docs/superpowers/specs/2026-11-19-fhir-federation-service-design.md) explains how a result keeps its identity across shards.

| If you want to… | Start here |
| --- | --- |
| Run the gateway and understand what a green probe proves | [Operate a gateway](operator.md) |
| Consume search results without confusing shard identities | [Build a client](client-developer-guide.md) |
| Find out whether a query works over HTTP, in Core only, or not yet | [Search support](status.md) |
| Look up configuration keys and their defaults | [Configuration reference](configuration.md) |
| Implement against the shard interface | [Shard API reference](contracts.md) |

These pages describe [the source revision used for this build](https://github.com/brendankowitz/fhir-federation-service/commit/REVISION). The public site follows `main`; it is not a versioned release manual.
