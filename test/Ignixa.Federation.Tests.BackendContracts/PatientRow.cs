using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>The direction of a <c>_sort=_lastUpdated</c> request.</summary>
internal enum PatientSortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// The physical identity of one Patient version as the server reports it: id, version and the instant it
/// was last updated. The instant is compared as a point in time, never as text, because the same instant
/// can be spelled with different offsets. <see cref="Parse"/> is the only way to obtain one, so an
/// instance always holds a safe id and version and a timezone-bearing instant.
/// </summary>
internal sealed partial record PatientRow(string Id, string VersionId, DateTimeOffset Instant)
{
    /// <summary>A bounded, response-independent description: reserved id, version and the instant in UTC.</summary>
    public string Describe() => $"{Id} v{VersionId} {Instant.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}";

    /// <exception cref="BackendEvidenceException">The element is not a Patient with a safe id, version and timezone-bearing instant.</exception>
    public static PatientRow Parse(JsonElement resource)
    {
        if (SearchsetJson.StringMember(resource, "resourceType") != "Patient")
        {
            throw SearchsetJson.Incomplete("an entry is not a Patient");
        }

        if (SearchsetJson.StringMember(resource, "id") is not { } id || !SafeToken().IsMatch(id))
        {
            throw SearchsetJson.Incomplete("a Patient has no safe id");
        }

        if (!resource.TryGetProperty("meta", out JsonElement meta)
            || SearchsetJson.StringMember(meta, "versionId") is not { } version
            || !SafeToken().IsMatch(version))
        {
            throw SearchsetJson.Incomplete("a Patient has no safe version");
        }

        if (SearchsetJson.StringMember(meta, "lastUpdated") is not { } text
            || !Timestamp().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset instant))
        {
            throw SearchsetJson.Incomplete("a Patient has no timezone-bearing update instant");
        }

        return new PatientRow(id, version, instant);
    }

    /// <summary>Parses a direct-read response body and requires it to be the Patient <paramref name="expectedId"/>.</summary>
    /// <exception cref="BackendEvidenceException">The body is not that Patient.</exception>
    public static PatientRow ParseRead(string json, string expectedId)
    {
        PatientRow row;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            row = Parse(document.RootElement);
        }
        catch (JsonException)
        {
            throw SearchsetJson.Incomplete("a direct read is not valid JSON");
        }

        return row.Id == expectedId ? row : throw SearchsetJson.Incomplete("a direct read returned a different Patient");
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-.]{1,64}\z")]
    private static partial Regex SafeToken();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z")]
    private static partial Regex Timestamp();
}
