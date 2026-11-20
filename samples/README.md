# Samples

## Ignixa.Federation.SampleClient

A standalone .NET console client for the federation's public wire contract: it searches, follows `next` links, treats
`fullUrl` as identity, and reads a match through the federation or directly from its shard. It depends only on the
wire contract in the design spec and references no server assembly.

Its tests run against scripted HTTP responses only (synthetic hosts, fake tokens, no network). No live federation
search exists yet, so the sample has not been run against a real federation or real shards.

See [Ignixa.Federation.SampleClient/README.md](Ignixa.Federation.SampleClient/README.md).
