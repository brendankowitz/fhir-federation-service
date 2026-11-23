---
title: Follow opaque search pages
sidebar_position: 4
description: Traverse federation continuations safely, distinguish stopping from completion, and restart an expired search.
---

# Follow opaque search pages

Keep two things separate: the **original parameters**, which you own and may use to start over, and the
**continuation link**, which the host owns. Do not derive an offset from the page number. One federation page
can depend on progress across several shards; a backend cursor is not a federation link.

## Request exactly the next page offered

1. Process the matches, includes and outcome issues in the current Bundle.
2. Find the Bundle-level link with `relation: "next"`. No such link means traversal is complete. A short or
   empty page does **not** mean completion if it carries `next`. `_count` limits matches, not all entries.
3. Resolve a relative URL against the configured federation base and validate it is in scope. If it repeats
   a previously followed URL, stop with an error rather than looping.
4. GET that URL with the federation credential for the same caller. Do not decode or log `ct`, append filters,
   rebuild the query, or forward the token to a shard.

For example, the [synthetic first page](client-http.md) leads to the following request on the public base
(placeholders only):

```http
GET /federation/Patient?ct=EXAMPLE HTTP/1.1
Host: fhir.example.test
Authorization: Bearer REDACTED
```

This is a GET even if the first page came from a form POST. `ct` must be the only parameter. The scoped URL,
not a byte-for-byte copy of a string, is what the .NET client uses: `System.Uri` may normalize an escape.
See the [client loop](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/FederationClient.cs)
and the [host continuation tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchContinuationTests.cs).

## Stop early without calling it a complete search

Enumeration is lazy: asking for the next `SearchPage` performs the next request. This sample-assembly example
consumes at most two pages, reports whether more exist, and does not expose the link:

```csharp
int pages = 0;
await foreach (SearchPage page in client.SearchAsync("Patient", [new("_count", "1")]))
{
    Console.WriteLine($"Page {++pages}: {page.Matches.Count} matches");
    if (pages == 2 && page.HasNext)
    {
        Console.WriteLine("Stopped early; more matches may exist.");
        break;
    }
}
```

The CLI's `--max-pages` does the same kind of bounded traversal; a successful exit can still be an incomplete
search. In the [CLI tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/CliAppTests.cs)
the limit is reached without fetching the next page. Use the [collection pattern](client-bundles.md) when you
actually want to consume every page.

## Recover without replaying a cursor

A `410 Gone` **on a continuation** ends that search. `SearchExpiredException` (CLI exit `3`) means start a new
search with your original parameters and fresh result collections; the sample does not retry or restart for you.
Do not silently combine the prefix of the old result with the suffix of a new one. A malformed continuation can
instead get `400`, and a continuation belonging to a different caller can get `403`. They are not instructions to
keep trying. The [host](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs)
currently issues inline continuations: if a required next link cannot fit, it can return `503`, not a successful
final page. For the same status on a **read**, use the [error guide](client-errors.md).
