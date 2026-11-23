using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Unicode;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Responses;

/// <summary>
/// Shapes one federated page into a FHIR R4 <c>searchset</c> <c>Bundle</c> (design spec §7.1, §7.2, §11). It formats
/// what the pipeline already selected and authorised; it checks JSON framing, not FHIR resource semantics.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The integration test project exercises this type; the host project has no InternalsVisibleTo.")]
public static class SearchsetResponse
{
    private const string ContentType = "application/fhir+json; charset=utf-8";

    /// <summary>
    /// Builds the response for one page. The whole page is serialised into memory first, so any invalid input throws
    /// here and no HTTP response state has been touched; the returned result writes the complete bytes in one step.
    /// </summary>
    /// <param name="registry">The registry that owns every shard in <paramref name="matches"/> and <paramref name="includes"/>.</param>
    /// <param name="self">
    /// The trusted absolute federation <c>self</c> link with the applied predicates. Written as supplied
    /// (<see cref="Uri.OriginalString"/>); do not pass an unprocessed incoming request URL.
    /// </param>
    /// <param name="matches">The page's matches, in emission order; each is written with <c>search.mode=match</c>.</param>
    /// <param name="includes">The page's included resources, in order; each is written with <c>search.mode=include</c>.</param>
    /// <param name="outcomes">
    /// Prebuilt federation <c>OperationOutcome</c> JSON objects, in order; each is written with
    /// <c>search.mode=outcome</c> and a bundle-local <c>urn:uuid:</c> full URL, without an alternate link or
    /// source-shard extension.
    /// </param>
    /// <param name="next">
    /// The trusted absolute opaque federation <c>next</c> link, or <see langword="null"/> on a terminal or count-only page.
    /// Never a backend cursor or link. Written as supplied.
    /// </param>
    /// <param name="proxyReadBase">
    /// The trusted absolute federation base URL without a query or fragment, when <c>ProxyReads</c> is enabled; its presence adds the
    /// <c>relation=alternate</c> link <c>{base}/shards/{alias}/{type}/{id}</c> to each match and include. The
    /// <c>source-shard</c> extension is written either way.
    /// </param>
    /// <param name="total">
    /// The R4 <c>Bundle.total</c> (an unsigned integer) when the caller has established one, including a count-only
    /// total; otherwise <see langword="null"/>. The page's entry count is never substituted.
    /// </param>
    /// <returns>A <c>200</c> <c>application/fhir+json</c> result carrying the serialised page.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="total"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// An entry's shard is not in <paramref name="registry"/>, or an outcome is not a single JSON object in valid UTF-8.
    /// </exception>
    /// <exception cref="SearchsetEntryException">
    /// A match's or include's resource is not a single JSON object in valid UTF-8; it names that entry's key.
    /// </exception>
    public static IResult Create(
        ShardRegistry registry,
        Uri self,
        IReadOnlyList<ShardEntry> matches,
        IReadOnlyList<ShardEntry> includes,
        IReadOnlyList<ReadOnlyMemory<byte>> outcomes,
        Uri? next = null,
        Uri? proxyReadBase = null,
        int? total = null) =>
        Create(registry, self, matches, includes, outcomes, next, proxyReadBase, static _ => true, total);

    /// <summary>
    /// Builds the response for one page like the public overload, but adds the <c>alternate</c> link only to an entry whose
    /// resource type <paramref name="alternateEligible"/> accepts. An entry that is not offered a link is otherwise unchanged:
    /// same <c>fullUrl</c>, source-shard extension and exact resource bytes.
    /// </summary>
    /// <param name="registry">See the public overload.</param>
    /// <param name="self">See the public overload.</param>
    /// <param name="matches">See the public overload.</param>
    /// <param name="includes">See the public overload.</param>
    /// <param name="outcomes">See the public overload.</param>
    /// <param name="next">See the public overload.</param>
    /// <param name="proxyReadBase">See the public overload; without it no entry has a link and <paramref name="alternateEligible"/> is not consulted.</param>
    /// <param name="alternateEligible">Whether the owner-qualified read serves a resource type.</param>
    /// <param name="total">See the public overload.</param>
    /// <returns>A <c>200</c> <c>application/fhir+json</c> result carrying the serialised page.</returns>
    internal static IResult Create(
        ShardRegistry registry,
        Uri self,
        IReadOnlyList<ShardEntry> matches,
        IReadOnlyList<ShardEntry> includes,
        IReadOnlyList<ReadOnlyMemory<byte>> outcomes,
        Uri? next,
        Uri? proxyReadBase,
        Func<ResourceType, bool> alternateEligible,
        int? total)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (total is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), total, "An R4 Bundle.total is an unsigned integer.");
        }

        string? alternateBase = proxyReadBase?.OriginalString.TrimEnd('/');
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Bundle");
            writer.WriteString("type", "searchset");
            if (total is { } count)
            {
                writer.WriteNumber("total", count);
            }

            writer.WriteStartArray("link");
            WriteLink(writer, "self", self.OriginalString);
            if (next is not null)
            {
                WriteLink(writer, "next", next.OriginalString);
            }

            writer.WriteEndArray();

            if (matches.Count > 0 || includes.Count > 0 || outcomes.Count > 0)
            {
                writer.WriteStartArray("entry");
                WriteShardEntries(writer, registry, matches, "match", alternateBase, alternateEligible, nameof(matches));
                WriteShardEntries(writer, registry, includes, "include", alternateBase, alternateEligible, nameof(includes));
                for (int i = 0; i < outcomes.Count; i++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("fullUrl", $"urn:uuid:{Guid.NewGuid():D}");
                    WriteResource(writer, outcomes[i].Span, nameof(outcomes), i, key: null);
                    WriteSearchMode(writer, "outcome");
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Results.Bytes(buffer.WrittenMemory, ContentType);
    }

    private static void WriteLink(Utf8JsonWriter writer, string relation, string url)
    {
        writer.WriteStartObject();
        writer.WriteString("relation", relation);
        writer.WriteString("url", url);
        writer.WriteEndObject();
    }

    private static void WriteShardEntries(
        Utf8JsonWriter writer,
        ShardRegistry registry,
        IReadOnlyList<ShardEntry> entries,
        string mode,
        string? alternateBase,
        Func<ResourceType, bool> alternateEligible,
        string sequence)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            ShardEntry entry = entries[i];
            ResourceKey key = entry.Key;
            if (!registry.TryGetById(key.Shard, out ShardDescriptor? shard))
            {
                throw new ArgumentException($"{sequence}[{i}] belongs to shard '{key.Shard}', which is not in the registry.", sequence);
            }

            writer.WriteStartObject();

            writer.WriteStartArray("extension");
            writer.WriteStartObject();
            writer.WriteString("url", FederationExtensions.SourceShardUrl);
            writer.WriteString("valueString", shard.Alias.Value);
            writer.WriteEndObject();
            writer.WriteEndArray();

            // An id of "." or ".." is a valid id but not addressable as a path segment, so it has no proxy path.
            if (alternateBase is not null && alternateEligible(key.Type) && key.Id.Value is not ("." or ".."))
            {
                writer.WriteStartArray("link");
                WriteLink(writer, "alternate", $"{alternateBase}/shards/{shard.Alias.Value}/{key.Type.Value}/{key.Id.Value}");
                writer.WriteEndArray();
            }

            writer.WriteString("fullUrl", registry.FullUrl(key));
            WriteResource(writer, entry.Resource.Span, sequence, i, key);
            WriteSearchMode(writer, mode);
            writer.WriteEndObject();
        }
    }

    private static void WriteSearchMode(Utf8JsonWriter writer, string mode)
    {
        writer.WriteStartObject("search");
        writer.WriteString("mode", mode);
        writer.WriteEndObject();
    }

    // A shard entry passes its key, so a rejection names the shard (a SearchsetEntryException); an outcome passes null.
    private static void WriteResource(Utf8JsonWriter writer, ReadOnlySpan<byte> json, string sequence, int index, ResourceKey? key)
    {
        if (!StartsWithObject(json))
        {
            throw Rejected(key, $"{sequence}[{index}] must be a JSON object.", sequence, null);
        }

        // WriteRawValue checks JSON syntax but can accept invalid UTF-8 inside strings.
        if (!Utf8.IsValid(json))
        {
            throw Rejected(key, $"{sequence}[{index}] is not valid UTF-8.", sequence, null);
        }

        writer.WritePropertyName("resource");
        try
        {
            writer.WriteRawValue(json, skipInputValidation: false);
        }
        catch (JsonException ex)
        {
            throw Rejected(key, $"{sequence}[{index}] is not a single valid JSON value.", sequence, ex);
        }
    }

    private static ArgumentException Rejected(ResourceKey? key, string message, string sequence, Exception? innerException) =>
        key is null
            ? new ArgumentException(message, sequence, innerException)
            : new SearchsetEntryException(key, message, sequence, innerException);

    private static bool StartsWithObject(ReadOnlySpan<byte> json)
    {
        int start = json.IndexOfAnyExcept(" \t\r\n"u8);
        return start >= 0 && json[start] == (byte)'{';
    }
}
