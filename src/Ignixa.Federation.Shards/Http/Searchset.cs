using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// The parts of a FHIR R4 JSON <c>searchset</c> Bundle the client uses, read strictly: a body that cannot be represented
/// exactly as a <see cref="ShardPage"/> is refused, never partially read (design spec §7.3, §8).
/// </summary>
/// <remarks>
/// Entry identity comes only from the shard that answered and the resource's own <c>resourceType</c> and <c>id</c>.
/// <c>entry.fullUrl</c> is ignored: backends emit it in forms that do not identify the shard (Ignixa 0.6.82 emits the
/// relative <c>Patient/dup</c>), and nothing in a response may redirect identity or a request.
/// </remarks>
internal sealed partial class Searchset
{
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    // Stands in for an absent array member; Bundle.link and Bundle.entry are optional.
    private static readonly JsonElement EmptyArray = JsonElement.Parse("[]");

    private static readonly string[] InstantFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    private Searchset(List<ShardEntry> entries, long? total, string? self, string? next)
    {
        Entries = entries;
        Total = total;
        Self = self;
        Next = next;
    }

    public IReadOnlyList<ShardEntry> Entries { get; }

    public long? Total { get; }

    /// <summary>Gets the one <c>self</c> link's url, or <see langword="null"/> when there is none.</summary>
    public string? Self { get; }

    /// <summary>Gets the one <c>next</c> link's url, or <see langword="null"/> when there is none.</summary>
    public string? Next { get; }

    /// <summary>Reads a searchset of <paramref name="type"/> resources returned by <paramref name="shard"/>.</summary>
    /// <exception cref="ShardCallException">The body is not a searchset this client can represent exactly.</exception>
    public static Searchset Read(ReadOnlyMemory<byte> body, ShardDescriptor shard, ResourceType type)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, Strict);
            return Read(document.RootElement, shard, type);
        }
        catch (JsonException)
        {
            throw Malformed(shard, "The body is not valid JSON.");
        }
    }

    private static Searchset Read(JsonElement bundle, ShardDescriptor shard, ResourceType type)
    {
        if (bundle.ValueKind != JsonValueKind.Object
            || !IsString(bundle, "resourceType", "Bundle")
            || !IsString(bundle, "type", "searchset"))
        {
            throw Malformed(shard, "The body is not a searchset Bundle.");
        }

        long? total = null;
        if (bundle.TryGetProperty("total", out JsonElement totalElement))
        {
            total = totalElement.ValueKind == JsonValueKind.Number && totalElement.TryGetInt64(out long count) && count >= 0
                ? count
                : throw Malformed(shard, "Bundle.total is not a non-negative integer.");
        }

        (string? self, string? next) = ReadLinks(bundle, shard);
        return new Searchset(ReadEntries(bundle, shard, type), total, self, next);
    }

    private static (string? Self, string? Next) ReadLinks(JsonElement bundle, ShardDescriptor shard)
    {
        string? self = null;
        string? next = null;
        foreach (JsonElement link in ArrayProperty(bundle, "link", shard))
        {
            if (link.ValueKind != JsonValueKind.Object
                || !link.TryGetProperty("relation", out JsonElement relationElement) || Text(relationElement) is not { } relation
                || !link.TryGetProperty("url", out JsonElement urlElement) || Text(urlElement) is not { } url)
            {
                throw Malformed(shard, "A Bundle.link is not a relation and url.");
            }

            // Registered relation types compare case-insensitively (RFC 8288 §2.1.1), so "Next" is a next link, never an
            // unknown relation that would make the page look exhausted.
            if (string.Equals(relation, "self", StringComparison.OrdinalIgnoreCase))
            {
                self = self is null ? NonEmpty(url, shard) : throw Malformed(shard, "The Bundle has more than one self link.");
            }
            else if (string.Equals(relation, "next", StringComparison.OrdinalIgnoreCase))
            {
                next = next is null ? NonEmpty(url, shard) : throw Malformed(shard, "The Bundle has more than one next link.");
            }
        }

        return (self, next);
    }

    private static List<ShardEntry> ReadEntries(JsonElement bundle, ShardDescriptor shard, ResourceType type)
    {
        var entries = new List<ShardEntry>();
        foreach (JsonElement entry in ArrayProperty(bundle, "entry", shard))
        {
            // include and outcome entries have no place in a ShardPage; dropping them would change the answer.
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("search", out JsonElement search)
                || search.ValueKind != JsonValueKind.Object
                || !IsString(search, "mode", "match"))
            {
                throw Malformed(shard, "An entry is not a search match.");
            }

            if (!entry.TryGetProperty("resource", out JsonElement resource)
                || resource.ValueKind != JsonValueKind.Object
                || !IsString(resource, "resourceType", type.Value))
            {
                throw Malformed(shard, "An entry is not a resource of the searched type.");
            }

            if (!resource.TryGetProperty("id", out JsonElement idElement)
                || !ResourceId.TryParse(Text(idElement), out ResourceId? id))
            {
                throw Malformed(shard, "An entry resource has no valid id.");
            }

            (string? versionId, DateTimeOffset? lastUpdated) = ReadMeta(resource, shard);
            entries.Add(new ShardEntry(
                new ResourceKey(shard.Id, type, id),
                versionId,
                lastUpdated,
                JsonMarshal.GetRawUtf8Value(resource)));
        }

        return entries;
    }

    private static (string? VersionId, DateTimeOffset? LastUpdated) ReadMeta(JsonElement resource, ShardDescriptor shard)
    {
        if (!resource.TryGetProperty("meta", out JsonElement meta))
        {
            return (null, null);
        }

        if (meta.ValueKind != JsonValueKind.Object)
        {
            throw Malformed(shard, "An entry resource meta is not an object.");
        }

        string? versionId = null;
        if (meta.TryGetProperty("versionId", out JsonElement version))
        {
            versionId = Text(version) is { Length: > 0 } text
                ? text
                : throw Malformed(shard, "An entry resource meta.versionId is not a non-empty string.");
        }

        DateTimeOffset? lastUpdated = null;
        if (meta.TryGetProperty("lastUpdated", out JsonElement instant))
        {
            lastUpdated = Text(instant) is { } text && TryParseInstant(text, out DateTimeOffset value)
                ? value
                : throw Malformed(shard, "An entry resource meta.lastUpdated is not a representable FHIR instant.");
        }

        return (versionId, lastUpdated);
    }

    // A FHIR instant always has seconds and a zone. DateTimeOffset holds at most 7 fractional digits and no leap second,
    // so a finer or leap instant is refused rather than rounded into a different order position.
    private static bool TryParseInstant(string text, out DateTimeOffset value)
    {
        value = default;
        return InstantShape().IsMatch(text)
            && DateTimeOffset.TryParseExact(text, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static JsonElement.ArrayEnumerator ArrayProperty(JsonElement bundle, string name, ShardDescriptor shard)
    {
        if (!bundle.TryGetProperty(name, out JsonElement array))
        {
            return EmptyArray.EnumerateArray();
        }

        return array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : throw Malformed(shard, $"Bundle.{name} is not an array.");
    }

    private static string NonEmpty(string url, ShardDescriptor shard) =>
        url.Length > 0 ? url : throw Malformed(shard, "A self or next link has an empty url.");

    private static bool IsString(JsonElement owner, string name, string expected) =>
        owner.TryGetProperty(name, out JsonElement value) && string.Equals(Text(value), expected, StringComparison.Ordinal);

    // Every string the client consumes is decoded here. Valid JSON text can escape an unpaired surrogate ("\ud800"), which
    // has no UTF-16 string form: GetString and ValueEquals throw InvalidOperationException for it, so it reads as absent
    // and the caller refuses the body. The kind is checked first, so the catch covers only that decoding failure.
    private static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static ShardCallException Malformed(ShardDescriptor shard, string reason) =>
        new(shard, ShardCallFailure.MalformedResponse, reason);

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantShape();
}
