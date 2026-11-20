using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.Integration.Oracle;

/// <summary>
/// Compares the searchset pages a harness actually received against a bound case's expected answers
/// (fixture README, "Harness contract" items 3 and 5-8). It never evaluates a query: expected answers come
/// only from the manifest and actual results only from the supplied pages.
/// </summary>
internal static class OraclePageComparator
{
    /// <param name="federationPages">The federation's pages in order, already followed via <c>next</c>.</param>
    /// <param name="oraclePages">The oracle's independently paged pages; required for unique-id cases, absent for collision cases.</param>
    /// <returns>Every failure, each formatted <c>[caseId] system[ page n]: problem</c>; empty means pass.</returns>
    public static IReadOnlyList<string> Compare(BoundCase c, IReadOnlyList<JsonNode> federationPages, IReadOnlyList<JsonNode>? oraclePages)
    {
        var failures = new List<string>();
        new SystemRun(c, "federation", isOracle: false, c.ShardBases, c.ExpectedFederationMatches, failures).Check(federationPages);

        if (c.Corpus == OracleCorpus.Collision)
        {
            if (oraclePages is { Count: > 0 })
            {
                failures.Add($"[{c.Id}] oracle: oracle is not used for collision cases, but {oraclePages.Count} page(s) were supplied");
            }
        }
        else
        {
            // Binding guarantees unique-id cases carry the oracle base and expected oracle matches.
            new SystemRun(c, "oracle", isOracle: true, [c.OracleBase!], c.ExpectedOracleMatches!, failures).Check(oraclePages ?? []);
        }

        return failures;
    }

    /// <param name="Identity"><c>Type/id</c> from the body for unique-id cases; <paramref name="FullUrl"/> for collision cases.</param>
    /// <param name="FullUrl">The returned fullUrl in <see cref="OracleUrl"/> canonical form, or as returned when it has none.</param>
    private sealed record Entry(string Identity, string FullUrl, string TypeId, JsonObject Resource);

    private sealed record Page(int Number, IReadOnlyList<Entry> Matches, IReadOnlyList<Entry> Includes);

    /// <summary>Checks one system's pages; the federation and the oracle are judged independently.</summary>
    private sealed class SystemRun(
        BoundCase c,
        string name,
        bool isOracle,
        IReadOnlyList<SeededBase> bases,
        IReadOnlyList<string> expectedMatches,
        List<string> failures)
    {
        public void Check(IReadOnlyList<JsonNode> pages)
        {
            if (pages.Count == 0)
            {
                Fail(null, "no pages supplied; each system needs at least one page");
                return;
            }

            var read = pages.Select((page, index) => ReadPage(page, index + 1, isLast: index == pages.Count - 1)).ToList();
            CheckMatches(read);
            CheckIncludes(read);
            CheckCoEmission(read);
            CheckBodies(read);
        }

        private Page ReadPage(JsonNode page, int number, bool isLast)
        {
            var matches = new List<Entry>();
            var includes = new List<Entry>();
            if (page is not JsonObject bundle)
            {
                Fail(number, "page is not a JSON object");
                return new Page(number, matches, includes);
            }

            var resourceType = Text(bundle, "resourceType");
            if (resourceType != "Bundle")
            {
                Fail(number, $"resourceType is '{resourceType}', expected 'Bundle'");
            }

            var type = Text(bundle, "type");
            if (type != "searchset")
            {
                Fail(number, $"type is '{type}', expected 'searchset'");
            }

            var hasNext = HasUsableNextLink(bundle, number);
            if (isLast && hasNext)
            {
                Fail(number, "last supplied page has a next link (page list truncated)");
            }
            else if (!isLast && !hasNext)
            {
                Fail(number, "no next link, but further pages were supplied (page list over-followed)");
            }

            switch (bundle["entry"])
            {
                case null:
                    break;
                case JsonArray entries:
                    for (var index = 0; index < entries.Count; index++)
                    {
                        if (ReadEntry(entries[index], number, index + 1) is { } read)
                        {
                            (read.IsMatch ? matches : includes).Add(read.Entry);
                        }
                    }

                    break;
                default:
                    Fail(number, "entry is not an array");
                    break;
            }

            return new Page(number, matches, includes);
        }

        /// <summary>
        /// Contract item 5 paging evidence: <c>link</c>, when present, is an array of objects with a string relation,
        /// holding at most one <c>next</c> whose url is a nonblank string. The url is opaque and never followed here.
        /// Every defect is reported; a <c>next</c> without a usable url does not count as one.
        /// </summary>
        private bool HasUsableNextLink(JsonObject bundle, int page)
        {
            switch (bundle["link"])
            {
                case null:
                    return false;
                case JsonArray links:
                    var nextLinks = 0;
                    var usable = false;
                    for (var index = 0; index < links.Count; index++)
                    {
                        var relation = Text(links[index], "relation");
                        if (relation is null)
                        {
                            Fail(page, $"link {index + 1} is not an object with a string relation");
                        }
                        else if (relation == "next")
                        {
                            nextLinks++;
                            if (Text(links[index], "url") is { } url && !string.IsNullOrWhiteSpace(url))
                            {
                                usable = true;
                            }
                            else
                            {
                                Fail(page, $"link {index + 1} (next) url is not a nonblank string");
                            }
                        }
                    }

                    if (nextLinks > 1)
                    {
                        Fail(page, $"{nextLinks} next links, expected at most one");
                    }

                    return usable;
                default:
                    Fail(page, "link is not an array");
                    return false;
            }
        }

        private (Entry Entry, bool IsMatch)? ReadEntry(JsonNode? node, int page, int index)
        {
            var fullUrl = Text(node, "fullUrl");
            var resource = (node as JsonObject)?["resource"] as JsonObject;
            var resourceType = Text(resource, "resourceType");
            var id = Text(resource, "id");
            var mode = Text((node as JsonObject)?["search"], "mode");
            if (fullUrl is null || resource is null || resourceType is null || id is null || mode is null)
            {
                string?[] missing =
                [
                    fullUrl is null ? "fullUrl" : null,
                    resourceType is null ? "resource.resourceType" : null,
                    id is null ? "resource.id" : null,
                    mode is null ? "search.mode" : null,
                ];
                Fail(page, $"entry {index} is missing {string.Join(", ", missing.OfType<string>())}");
                return null;
            }

            if (mode is not ("match" or "include"))
            {
                Fail(page, $"entry {index} ({fullUrl}) has search.mode '{mode}', expected match or include");
                return null;
            }

            var typeId = $"{resourceType}/{id}";
            var canonicalUrl = OracleUrl.Canonicalize(fullUrl);
            CheckOwnership(page, fullUrl, canonicalUrl, typeId);

            // Spellings of one physical URL are one collision identity; an unparseable fullUrl keeps its raw text
            // so it still surfaces as an unexpected match rather than vanishing.
            var urlIdentity = canonicalUrl ?? fullUrl;
            var identity = c.Corpus == OracleCorpus.UniqueId ? typeId : urlIdentity;
            return (new Entry(identity, urlIdentity, typeId, resource), mode == "match");
        }

        /// <summary>Contract item 3: validate the returned fullUrl against the owning base before projecting.</summary>
        /// <param name="fullUrl">As returned, for diagnostics.</param>
        /// <param name="canonicalUrl">The <see cref="OracleUrl"/> spelling, comparable with the canonical bases; null if unusable.</param>
        private void CheckOwnership(int page, string fullUrl, string? canonicalUrl, string typeId)
        {
            if (canonicalUrl is null)
            {
                Fail(page, $"fullUrl {fullUrl} is not an absolute http(s) URL without userinfo");
                return;
            }

            var owner = bases
                .Where(candidate => canonicalUrl.StartsWith(candidate.BaseUrl, StringComparison.Ordinal)
                    && IsTypeIdPath(canonicalUrl[candidate.BaseUrl.Length..]))
                .MaxBy(candidate => candidate.BaseUrl.Length);
            if (owner is null)
            {
                Fail(page, $"fullUrl {fullUrl} is not <{(isOracle ? "oracle base" : "registered shard base")}>Type/id");
                return;
            }

            if (canonicalUrl[owner.BaseUrl.Length..] != typeId)
            {
                Fail(page, $"fullUrl {fullUrl} does not match body {typeId}");
                return;
            }

            if (!owner.Seeded.Contains(typeId))
            {
                var seededOn = bases.Where(candidate => candidate.Seeded.Contains(typeId)).Select(candidate => candidate.Owner).ToList();
                var ownerName = isOracle ? "the oracle" : $"shard '{owner.Owner}'";
                Fail(page, $"{fullUrl}: {typeId} is not seeded on {ownerName} (seeded on: {(seededOn.Count == 0 ? "none" : string.Join(", ", seededOn))})");
            }
        }

        /// <summary>Contract items 4 and 5: count bound, no repeats, multiset equality, negative control.</summary>
        private void CheckMatches(IReadOnlyList<Page> pages)
        {
            if (c.Count is int count)
            {
                foreach (var page in pages.Where(page => page.Matches.Count > count))
                {
                    Fail(page.Number, $"{page.Matches.Count} match entries exceed count {count}");
                }
            }

            var firstPage = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                foreach (var match in page.Matches)
                {
                    if (!firstPage.TryAdd(match.Identity, page.Number))
                    {
                        Fail(null, $"match {match.Identity} repeated on page {firstPage[match.Identity]} and page {page.Number}");
                    }
                }
            }

            var actual = Tally(pages.SelectMany(page => page.Matches).Select(match => match.Identity));
            var expected = Tally(expectedMatches);
            foreach (var identity in expected.Keys.Concat(actual.Keys).Distinct(StringComparer.Ordinal))
            {
                var want = expected.GetValueOrDefault(identity);
                var got = actual.GetValueOrDefault(identity);
                if (got < want)
                {
                    Fail(null, $"missing match {identity} (returned {got}, expected {want})");
                }
                else if (got > want)
                {
                    Fail(null, $"unexpected match {identity} (returned {got}, expected {want})");
                }
            }

            if (!c.IsNegativeControl)
            {
                return;
            }

            foreach (var missed in c.MissedVsOracle)
            {
                var returned = actual.ContainsKey(missed);
                if (!isOracle && returned)
                {
                    Fail(null, $"Passthrough negative control failed: returned {missed}, which the control must miss");
                }
                else if (isOracle && !returned)
                {
                    Fail(null, $"negative control not discriminating: oracle must return {missed}");
                }
            }
        }

        /// <summary>Contract item 6: each page's includes are exactly the union its own matches require.</summary>
        private void CheckIncludes(IReadOnlyList<Page> pages)
        {
            foreach (var page in pages)
            {
                var matchIdentities = page.Matches.Select(match => match.Identity).ToHashSet(StringComparer.Ordinal);
                var required = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var match in page.Matches)
                {
                    if (!c.IncludeRelations.TryGetValue(match.Identity, out var targets))
                    {
                        continue;
                    }

                    foreach (var target in targets.Where(target => !matchIdentities.Contains(target)))
                    {
                        if (!required.TryGetValue(target, out var requiredBy))
                        {
                            required[target] = requiredBy = [];
                        }

                        requiredBy.Add(match.Identity);
                    }
                }

                var returned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var include in page.Includes)
                {
                    if (!returned.Add(include.Identity))
                    {
                        Fail(page.Number, $"duplicate include {include.Identity}");
                    }
                }

                foreach (var (target, requiredBy) in required.Where(pair => !returned.Contains(pair.Key)))
                {
                    Fail(page.Number, $"missing include {target} (required by {string.Join(", ", requiredBy)})");
                }

                foreach (var include in page.Includes.Select(include => include.Identity).Distinct().Where(include => !required.ContainsKey(include)))
                {
                    Fail(page.Number, $"unexpected include {include}");
                }
            }
        }

        /// <summary>Contract item 7: all witnesses on exactly one page, which carries every witness include.</summary>
        private void CheckCoEmission(IReadOnlyList<Page> pages)
        {
            if (c.CoEmitted.Count == 0)
            {
                return;
            }

            var carrying = pages.Where(page => page.Matches.Any(match => c.CoEmitted.Contains(match.Identity))).ToList();
            if (carrying.Count == 0)
            {
                Fail(null, $"no page carries a co-emitted witness ({string.Join(", ", c.CoEmitted)}); the run is non-discriminating, not a pass");
                return;
            }

            if (carrying.Count > 1)
            {
                Fail(null, $"co-emitted witnesses split across pages {string.Join(", ", carrying.Select(page => page.Number))}; the run is non-discriminating, not a pass");
                return;
            }

            var witnessPage = carrying[0];
            foreach (var witness in c.CoEmitted.Where(witness => !witnessPage.Matches.Any(match => match.Identity == witness)))
            {
                Fail(witnessPage.Number, $"co-emission page is missing witness {witness}");
            }

            foreach (var witnessInclude in c.WitnessIncludes.Where(target => !witnessPage.Includes.Any(include => include.Identity == target)))
            {
                Fail(witnessPage.Number, $"co-emission page is missing witness include {witnessInclude}");
            }
        }

        /// <summary>Contract item 8: every emitted colliding Patient carries the given names of its own shard.</summary>
        private void CheckBodies(IReadOnlyList<Page> pages)
        {
            if (c.ExpectedGiven.Count == 0)
            {
                return;
            }

            var guarded = c.ExpectedGiven.Keys.Select(TypeIdOf).ToHashSet(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                foreach (var entry in page.Matches.Concat(page.Includes).Where(entry => guarded.Contains(entry.TypeId)))
                {
                    if (!c.ExpectedGiven.TryGetValue(entry.FullUrl, out var expectedGiven))
                    {
                        Fail(page.Number, $"{entry.TypeId} at unlisted fullUrl {entry.FullUrl} (not in expectedGiven)");
                        continue;
                    }

                    var given = FirstGiven(entry.Resource);
                    if (given is null || !given.SequenceEqual(expectedGiven, StringComparer.Ordinal))
                    {
                        Fail(page.Number, $"{entry.FullUrl}: name[0].given is {Format(given)}, expected {Format(expectedGiven)}");
                    }
                }
            }
        }

        private void Fail(int? page, string problem) =>
            failures.Add($"[{c.Id}] {name}{(page is int number ? $" page {number}" : string.Empty)}: {problem}");
    }

    private static bool IsTypeIdPath(string path) =>
        path.IndexOfAny(['?', '#']) < 0 && path.Split('/') is [{ Length: > 0 }, { Length: > 0 }];

    private static string TypeIdOf(string fullUrl) => fullUrl.Split('/') is [.., var type, var id] ? $"{type}/{id}" : fullUrl;

    private static List<string>? FirstGiven(JsonObject resource) =>
        resource["name"] is JsonArray { Count: > 0 } names && names[0] is JsonObject first && first["given"] is JsonArray given
            ? [.. given.Select(name => Text(name) ?? name?.ToJsonString() ?? "null")]
            : null;

    private static string Format(IReadOnlyList<string>? values) => values is null ? "missing" : $"[{string.Join(", ", values)}]";

    private static Dictionary<string, int> Tally(IEnumerable<string> identities) =>
        identities.CountBy(identity => identity, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);

    private static string? Text(JsonNode? node, string property) => node is JsonObject obj ? Text(obj[property]) : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
