using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

/// <summary>One page of a federated search.</summary>
public sealed class SearchPage
{
    public required IReadOnlyList<FederatedEntry> Matches { get; init; }

    public required IReadOnlyList<FederatedEntry> Includes { get; init; }

    /// <summary>Notes the federation attached to the page (for example, included resources were truncated).</summary>
    public required IReadOnlyList<OperationIssue> Outcomes { get; init; }

    /// <summary>Whether the federation offered a next page.</summary>
    public bool HasNext => NextLink is not null;

    /// <summary>
    /// The next-page URL as parsed by <see cref="Uri"/> (RFC 3986). Internal on purpose: it may carry a continuation
    /// token, so it is only ever followed by the client, never printed. The client does not decode, edit or rebuild it.
    /// </summary>
    internal Uri? NextLink { get; init; }

    private const string SourceShardExtensionUrl =
        "https://github.com/brendankowitz/fhir-federation-service/StructureDefinition/source-shard";

    /// <summary>Reads a searchset Bundle. Relative links are resolved against <paramref name="baseUrl"/>.</summary>
    internal static SearchPage Parse(JsonElement bundle, Uri baseUrl)
    {
        if (bundle.Str("resourceType") != "Bundle")
        {
            throw new InvalidDataException("The response is not a FHIR Bundle.");
        }

        List<FederatedEntry> matches = [];
        List<FederatedEntry> includes = [];
        List<OperationIssue> outcomes = [];

        foreach (var entry in bundle.Items("entry"))
        {
            var resource = entry.Child("resource");
            if (resource.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A Bundle entry has no resource.");
            }

            // search.mode defaults to "match" when absent.
            switch (entry.Child("search").Str("mode") ?? "match")
            {
                case "outcome":
                    if (resource.Str("resourceType") != "OperationOutcome")
                    {
                        throw new InvalidDataException("An outcome entry's resource is not an OperationOutcome.");
                    }

                    outcomes.AddRange(OperationIssue.ReadAll(resource, ReadSourceShard(entry)));
                    break;
                case "match":
                    matches.Add(ReadEntry(entry, resource, EntryMode.Match, baseUrl));
                    break;
                case "include":
                    includes.Add(ReadEntry(entry, resource, EntryMode.Include, baseUrl));
                    break;
                default:
                    throw new InvalidDataException("A Bundle entry has an unsupported search mode.");
            }
        }

        return new SearchPage
        {
            Matches = matches,
            Includes = includes,
            Outcomes = outcomes,
            NextLink = FindLink(bundle, "next", baseUrl),
        };
    }

    private static FederatedEntry ReadEntry(JsonElement entry, JsonElement resource, EntryMode mode, Uri baseUrl) => new()
    {
        FullUrl = Uri.TryCreate(entry.Str("fullUrl"), UriKind.Absolute, out var fullUrl)
            ? fullUrl
            : throw new InvalidDataException("A match or include entry has no absolute fullUrl."),
        ResourceType = resource.Str("resourceType") ?? throw new InvalidDataException("An entry resource has no resourceType."),
        Id = resource.Str("id") ?? throw new InvalidDataException("An entry resource has no id."),
        Mode = mode,
        SourceShard = ReadSourceShard(entry),
        Alternate = FindLink(entry, "alternate", baseUrl),
        Resource = resource.Clone(),
    };

    private static string? ReadSourceShard(JsonElement entry) =>
        entry.Items("extension")
            .FirstOrDefault(extension => extension.Str("url") == SourceShardExtensionUrl)
            .Str("valueString");

    private static Uri? FindLink(JsonElement owner, string relation, Uri baseUrl) =>
        owner.Items("link").FirstOrDefault(link => link.Str("relation") == relation).Str("url") is { } url
            ? Uri.TryCreate(baseUrl, url, out var resolved)
                ? resolved
                : throw new InvalidDataException($"A '{relation}' link is not a valid URI.")
            : null;
}
