---
title: Handle OperationOutcome and request failures
sidebar_position: 6
description: Distinguish warnings from failed searches and choose a recovery action from the status and request context.
---

# Handle OperationOutcome and request failures

First ask **where did the issue appear?** An `OperationOutcome` entry in a successful searchset and an
`OperationOutcome` returned instead of a searchset have different meanings. Do not discard valid matches on a
page merely because it carries a warning, or treat an HTTP failure as a successful empty search.

## Read a page warning

For example, this abbreviated entry from a synthetic successful Bundle warns that not all **includes** are
available:

```json
{
  "search": {"mode": "outcome"},
  "resource": {
    "resourceType": "OperationOutcome",
    "issue": [{"severity":"warning","code":"incomplete","diagnostics":"included resources truncated"}]
  }
}
```

Keep the matches, retain each issue, and let the consumer know the related-resource list is incomplete. The
[parser](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/samples/Ignixa.Federation.SampleClient/SearchPage.cs)
exposes issues in `SearchPage.Outcomes`. It also retains the source alias when the entry supplies one. See
[Bundle interpretation](client-bundles.md) for the distinction between per-page and collected results.

## Handle failure by status and request

On a failed continuation the host can instead send the following response, with no Bundle to consume:

```http
HTTP/1.1 410 Gone
Content-Type: application/fhir+json; charset=utf-8

{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"not-found","diagnostics":"The continuation is no longer valid; repeat the original search."}]}
```

The text and issue code come from the [host error responses](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/SearchOutcome.cs).
Choose recovery from status **and operation**, not by matching diagnostics strings:

| Status or condition | Next action |
| --- | --- |
| `400` invalid, not supported, or too costly | Correct or narrow the request. For an ambiguous bare read, use an entry's offered [read route](client-reads.md). |
| `401` / `403` | On `401`, check token validity and audience. On `403`, check the role's type grants, SMART scopes, or caller binding on a continuation. Replaying the unchanged request does not grant access. |
| `410` | On a continuation, restart from the [original parameters](client-paging.md). On a qualified read, the resource was deleted. |
| `413` / `414` / `415` | Reduce body or query size, or correct the media type. A form search is UTF-8; JSON is not a form body. |
| `501` for a valid but unimplemented search | Check [search support](status.md) and choose another supported query. Do not infer that all spec routes are implemented. |
| `502` / `503` / `504` | A backend failure, unavailability, or timeout is not a complete set of results. Surface the failure. On `503`, check host readiness; a required continuation that is too large can also cause `503`. |
| Redirect, malformed success body, or non-FHIR error body | Do not follow a redirect or parse arbitrary text as a Bundle. Preserve status when available; do not log the raw body. |

These are [search handler](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearch.cs)
behaviors, not a promise that every failure has a JSON body. For example, unmapped history, vread, operation
or write paths can reach the router's empty `404` or `405`.

## Translate failures for a CLI or library caller

The sample CLI returns `0` on success, `2` for invalid usage, `3` for expired continuation, `4` for HTTP or
response failures, and `5` for a refused link or missing requested alternate. A repeated next link is a paging
error (`4`), not an expired search. See the [CLI tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.SampleClient.UnitTests/CliAppTests.cs).

In library code, catch `SearchExpiredException` **before** `FederationRequestException` (its base class).
When the response isn't an `OperationOutcome`, `FederationRequestException.Issues` can be empty; the status
still matters. Handle exceptions around the `await foreach` or `FromAsync` call where I/O takes place, not just
around creation of the enumerable. Log only what you need: identifiers and server diagnostics can be sensitive
even when tokens and query strings are omitted.
