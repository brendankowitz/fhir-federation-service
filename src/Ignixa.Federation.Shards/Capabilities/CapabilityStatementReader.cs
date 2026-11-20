using System.Collections.Immutable;
using System.Text.Json;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// Reads one shard's already-received R4 <c>CapabilityStatement</c> into <see cref="ShardCapabilityEvidence"/> (design
/// spec §9). A pure function of the bytes: no network, credentials, FHIR SDK, URI dereference, caching or intersection.
/// </summary>
/// <remarks>
/// <para>Only the fields the evidence needs are read and checked; this is not a schema validator. What it does read is read
/// strictly and fails closed: a relevant field of the wrong shape, a duplicate JSON property, a second server block, a
/// repeated resource declaration or two parameters of one code that disagree is refused with a
/// <see cref="FormatException"/>, never resolved by first-wins, last-wins or union. Wildcard include declarations are
/// dropped, never expanded; any other include value the reader cannot use is refused, so it can never add evidence.</para>
/// <para>The exception message is fixed text naming a location by member names and indexes only. It never contains input
/// text, and there is no inner exception, so it is safe to log. The caller bounds the size of the body.</para>
/// </remarks>
public static class CapabilityStatementReader
{
    private const string Wildcard = "*";

    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>Reads a CapabilityStatement.</summary>
    /// <param name="utf8Json">The UTF-8 JSON body of the shard's <c>/metadata</c> response.</param>
    /// <returns>What the statement advertises, as evidence only.</returns>
    /// <exception cref="FormatException">
    /// The body is not an R4 (4.0.0 or 4.0.1) CapabilityStatement that advertises (<c>kind</c> other than
    /// <c>requirements</c>), or a field this reader consumes is malformed or ambiguous.
    /// </exception>
    public static ShardCapabilityEvidence Read(ReadOnlyMemory<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, Strict);
        }
        catch (JsonException)
        {
            throw Malformed("$", "The body is not valid JSON.");
        }

        using (document)
        {
            return Read(document.RootElement);
        }
    }

    private static ShardCapabilityEvidence Read(JsonElement statement)
    {
        const string Root = "$";
        if (statement.ValueKind != JsonValueKind.Object || RequiredString(statement, "resourceType", Root) != "CapabilityStatement")
        {
            throw Malformed(Root, "The body is not a CapabilityStatement.");
        }

        if (RequiredString(statement, "fhirVersion", Root) is not ("4.0.0" or "4.0.1"))
        {
            throw Malformed(Root, "fhirVersion is not R4 (4.0.0 or 4.0.1).");
        }

        // A statement of requirements says what a server shall do, not what this one does, so none of it is advertisement.
        if (RequiredString(statement, "kind", Root) is not ("instance" or "capability"))
        {
            throw Malformed(Root, "kind is not instance or capability.");
        }

        SoftwareEvidence? software = ReadSoftware(statement);
        (JsonElement Block, string Path)? server = FindServerBlock(statement);
        if (server is not var (block, serverPath))
        {
            return new ShardCapabilityEvidence(ImmutableDictionary<ResourceType, ResourceSearchEvidence>.Empty, [], software);
        }

        return new ShardCapabilityEvidence(
            ReadResources(block, serverPath),
            ReadSearchParameters(block, serverPath),
            software);
    }

    private static SoftwareEvidence? ReadSoftware(JsonElement statement)
    {
        if (!statement.TryGetProperty("software", out JsonElement software))
        {
            return null;
        }

        const string Path = "$.software";
        if (software.ValueKind != JsonValueKind.Object)
        {
            throw Malformed(Path, "software is not an object.");
        }

        string? name = OptionalString(software, "name", Path);
        string? version = OptionalString(software, "version", Path);
        return name is null && version is null ? null : new SoftwareEvidence(name, version);
    }

    private static (JsonElement Block, string Path)? FindServerBlock(JsonElement statement)
    {
        (JsonElement, string)? server = null;
        foreach ((JsonElement block, string path) in Elements(statement, "rest", "$"))
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(path, "A rest entry is not an object.");
            }

            switch (RequiredString(block, "mode", path))
            {
                case "server":
                    server = server is null ? (block, path) : throw Malformed(path, "The statement has more than one server rest block.");
                    break;
                case "client":
                    break;
                default:
                    throw Malformed(path, "mode is not server or client.");
            }
        }

        return server;
    }

    private static ImmutableDictionary<ResourceType, ResourceSearchEvidence> ReadResources(JsonElement server, string serverPath)
    {
        ImmutableDictionary<ResourceType, ResourceSearchEvidence>.Builder searchable =
            ImmutableDictionary.CreateBuilder<ResourceType, ResourceSearchEvidence>();
        var declared = new HashSet<ResourceType>();
        foreach ((JsonElement resource, string path) in Elements(server, "resource", serverPath))
        {
            if (resource.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(path, "A resource entry is not an object.");
            }

            if (!ResourceType.TryParse(RequiredString(resource, "type", path), out ResourceType? type))
            {
                throw Malformed(path, "type is not a resource type name.");
            }

            // Merging two declarations would credit the type with parameters neither declaration claims alone.
            if (!declared.Add(type))
            {
                throw Malformed(path, "The resource type is declared more than once.");
            }

            if (AdvertisesSearchType(resource, path))
            {
                searchable.Add(type, new ResourceSearchEvidence(
                    type,
                    ReadSearchParameters(resource, path),
                    ReadInclusions(resource, "searchInclude", path),
                    ReadInclusions(resource, "searchRevInclude", path)));
            }
        }

        return searchable.ToImmutable();
    }

    private static bool AdvertisesSearchType(JsonElement resource, string path)
    {
        bool advertises = false;
        foreach ((JsonElement interaction, string interactionPath) in Elements(resource, "interaction", path))
        {
            if (interaction.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(interactionPath, "An interaction entry is not an object.");
            }

            advertises |= RequiredString(interaction, "code", interactionPath) == "search-type";
        }

        return advertises;
    }

    private static ImmutableArray<SearchParameterEvidence> ReadSearchParameters(JsonElement owner, string ownerPath)
    {
        ImmutableArray<SearchParameterEvidence>.Builder parameters = ImmutableArray.CreateBuilder<SearchParameterEvidence>();
        var byCode = new Dictionary<string, SearchParameterEvidence>(StringComparer.Ordinal);
        foreach ((JsonElement element, string path) in Elements(owner, "searchParam", ownerPath))
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(path, "A searchParam entry is not an object.");
            }

            string code = RequiredString(element, "name", path);
            SearchParameterType type = ParseType(RequiredString(element, "type", path), path);
            var parameter = new SearchParameterEvidence(code, type, ReadDefinition(element, path));

            // Two entries of one code are one fact only when they agree; otherwise neither can be trusted over the other.
            if (!byCode.TryGetValue(code, out SearchParameterEvidence? existing))
            {
                byCode.Add(code, parameter);
                parameters.Add(parameter);
            }
            else if (existing != parameter)
            {
                throw Malformed(path, "The same search parameter code is declared with conflicting type or definition.");
            }
        }

        return parameters.ToImmutable();
    }

    // An R4 canonical is a URI reference, absolute or relative, with an optional "|version" and then an optional "#fragment".
    // Only that lexical shape is checked: the value is neither dereferenced nor matched, and its spelling is kept as sent.
    private static string? ReadDefinition(JsonElement parameter, string path)
    {
        string? definition = OptionalString(parameter, "definition", path);
        return definition is null || IsCanonical(definition)
            ? definition
            : throw Malformed(path, "definition is not a canonical.");
    }

    private static bool IsCanonical(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool badEscape = c == '%' && (i + 2 >= value.Length || !char.IsAsciiHexDigit(value[i + 1]) || !char.IsAsciiHexDigit(value[i + 2]));
            if (char.IsWhiteSpace(c) || char.IsControl(c) || "<>\"\\^`{}".Contains(c, StringComparison.Ordinal) || badEscape)
            {
                return false;
            }
        }

        int hash = value.IndexOf('#', StringComparison.Ordinal);
        string beforeFragment = hash < 0 ? value : value[..hash];
        string? fragment = hash < 0 ? null : value[(hash + 1)..];
        if (fragment is not null && (fragment.Length == 0 || fragment.AsSpan().ContainsAny('#', '|')))
        {
            return false;
        }

        int bar = beforeFragment.IndexOf('|', StringComparison.Ordinal);
        string url = bar < 0 ? beforeFragment : beforeFragment[..bar];
        if (bar < 0)
        {
            return url.Length > 0;
        }

        string version = beforeFragment[(bar + 1)..];
        return url.Length > 0 && version.Length > 0 && !version.Contains('|', StringComparison.Ordinal);
    }

    private static SearchParameterType ParseType(string code, string path) => code switch
    {
        "number" => SearchParameterType.Number,
        "date" => SearchParameterType.Date,
        "string" => SearchParameterType.String,
        "token" => SearchParameterType.Token,
        "reference" => SearchParameterType.Reference,
        "composite" => SearchParameterType.Composite,
        "quantity" => SearchParameterType.Quantity,
        "uri" => SearchParameterType.Uri,
        "special" => SearchParameterType.Special,
        _ => throw Malformed(path, "type is not an R4 search parameter type."),
    };

    private static ImmutableArray<SearchInclusion> ReadInclusions(JsonElement resource, string member, string resourcePath)
    {
        ImmutableArray<SearchInclusion>.Builder inclusions = ImmutableArray.CreateBuilder<SearchInclusion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((JsonElement element, string path) in Elements(resource, member, resourcePath))
        {
            string value = Text(element) ?? throw Malformed(path, "An include value is not a string.");
            if (ParseInclusion(value, path) is { } inclusion && seen.Add(value))
            {
                inclusions.Add(inclusion);
            }
        }

        return inclusions.ToImmutable();
    }

    // Returns null for a wildcard, which is not evidence of any specific include and is never expanded.
    private static SearchInclusion? ParseInclusion(string value, string path)
    {
        string[] parts = value.Split(':');
        if (parts.Length <= 3 && Array.IndexOf(parts, Wildcard) >= 0)
        {
            return null;
        }

        if (parts.Length is not (2 or 3)
            || !ResourceType.TryParse(parts[0], out ResourceType? source)
            || !IsParameterCode(parts[1]))
        {
            throw Malformed(path, "An include value is not Type:parameter or Type:parameter:Target.");
        }

        ResourceType? target = null;
        if (parts.Length == 3 && !ResourceType.TryParse(parts[2], out target))
        {
            throw Malformed(path, "An include value's target is not a resource type name.");
        }

        return new SearchInclusion(value, source, parts[1], target);
    }

    private static bool IsParameterCode(string code) =>
        code.Length > 0 && !code.Any(c => char.IsWhiteSpace(c) || c == '*');

    // Elements of an optional array member, with a location for diagnostics made only of member names and indexes.
    private static IEnumerable<(JsonElement Element, string Path)> Elements(JsonElement owner, string member, string ownerPath)
    {
        if (!owner.TryGetProperty(member, out JsonElement array))
        {
            yield break;
        }

        string arrayPath = $"{ownerPath}.{member}";
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Malformed(arrayPath, $"{member} is not an array.");
        }

        int index = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            yield return (element, $"{arrayPath}[{index}]");
            index++;
        }
    }

    private static string RequiredString(JsonElement owner, string member, string ownerPath) =>
        OptionalString(owner, member, ownerPath) ?? throw Malformed(ownerPath, $"{member} is missing.");

    // A reported string must say something: empty and blank are malformed, never "absent".
    private static string? OptionalString(JsonElement owner, string member, string ownerPath)
    {
        if (!owner.TryGetProperty(member, out JsonElement element))
        {
            return null;
        }

        return Text(element) is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw Malformed(ownerPath, $"{member} is not a non-empty string.");
    }

    // Valid JSON can escape an unpaired surrogate ("\ud800"), which has no UTF-16 string form: GetString throws
    // InvalidOperationException for it, so it reads as not-a-string and the caller refuses the body. The kind is checked
    // first, so the catch covers only that decoding failure.
    private static string? Text(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static FormatException Malformed(string path, string reason) =>
        new($"CapabilityStatement at {path}: {reason}");
}
