---
title: Client developer guide
sidebar_position: 1
description: Build a read-only FHIR client that keeps shard identities, continuations, and credentials separate.
---

# Client developer guide

Suppose two shards each return `Patient/dup`. Which patient should appear in your results, and which server
should receive a follow-up read? **Keep both patients.** Use each entry's owning `fullUrl` as its identity.
The federation's [collision fixture](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/fixtures/federation-oracle/README.md)
makes the difference observable: east's patient is Eli Orchid; west's is Gus Orchid.

## Work through the collision

| Lesson | What you can do afterwards |
| --- | --- |
| 1. [Send a search](../site/client-http.md) | Choose GET or form POST, authenticate, and reproduce a first page with synthetic shards. |
| 2. [Interpret a searchset Bundle](../site/client-bundles.md) | Keep Eli and Gus distinct, avoid counting includes as hits, and preserve page warnings. |
| 3. [Follow pages](../site/client-paging.md) | Traverse opaque links, recognize an incomplete traversal, and restart after expiry. |
| 4. [Read a match](../site/client-reads.md) | Read from the owner or through the federation without sending a token to the wrong server. |
| 5. [Handle errors](../site/client-errors.md) | Tell a page warning from a failed request and decide what the caller can do next. |
| 6. [Run or adapt the .NET sample](../site/sample-client.md) | Exercise the CLI offline or copy the internal client pattern into your application. |

## What counts as success?

For the [oracle collision fixture](../site/client-bundles.md), run `Patient?name=Orchid&_count=1` and follow `next`
to exhaustion. Count raw `match` entries on **every page before aggregation**. Require exactly two matches in
total: `https://fhir-east.example.org/Patient/dup` once with given name `Eli`, and
`https://fhir-west.example.org/Patient/dup` once with given name `Gus`. Fail on any missing, duplicate or extra
match, even if deduplication would leave the expected two URLs. The CLI omits resource bodies; inspect the
entries in the page stream to check the names.

You can start with the [sample tests](../site/sample-client.md) without a service or credentials. They test how a client
reacts to canned responses. The [HTTP lesson](../site/client-http.md) instead exercises the merged API in memory with a
synthetic identity provider and scripted shard transport. Its four scripted patients exercise paging, **not**
the oracle's filtering expectation. Neither exercise certifies a deployment. A green health probe alone is not
evidence of a configured backend; see [Operate a gateway](../site/operator.md) and [Search support](../site/status.md).
