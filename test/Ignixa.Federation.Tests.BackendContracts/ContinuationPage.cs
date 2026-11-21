using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// One page of a paged Patient search: its Patient matches and the continuation it offers, if any. Unlike
/// <see cref="SearchsetJson.ParseCompleteSearchset"/> a <c>next</c> link is legal here. <see cref="Parse"/> is
/// the only way to obtain one, so an instance holds only Patient matches and an already-validated next link.
/// </summary>
internal sealed class ContinuationPage
{
    private ContinuationPage(IReadOnlyList<PatientRow> rows, NativeContinuation? next)
    {
        Rows = rows;
        Next = next;
    }

    public IReadOnlyList<PatientRow> Rows { get; }

    public NativeContinuation? Next { get; }

    /// <exception cref="BackendEvidenceException">The response is not a searchset of Patient matches with at most one safe next link.</exception>
    public static ContinuationPage Parse(string json, Uri baseAddress)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw SearchsetJson.Incomplete("response body is not valid JSON");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (SearchsetJson.StringMember(root, "resourceType") != "Bundle" || SearchsetJson.StringMember(root, "type") != "searchset")
            {
                throw SearchsetJson.Incomplete("response is not a searchset Bundle");
            }

            List<PatientRow> rows = [];
            foreach (JsonElement entry in SearchsetJson.Array(root, "entry"))
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("search", out JsonElement search)
                    || SearchsetJson.StringMember(search, "mode") != "match"
                    || !entry.TryGetProperty("resource", out JsonElement resource))
                {
                    throw SearchsetJson.Incomplete("an entry is not a search match with a resource");
                }

                rows.Add(PatientRow.Parse(resource));
            }

            NativeContinuation? next = null;
            foreach (JsonElement link in SearchsetJson.Array(root, "link"))
            {
                if (SearchsetJson.StringMember(link, "relation") is not { Length: > 0 } relation)
                {
                    throw SearchsetJson.Incomplete("a searchset link has no relation");
                }

                if (relation != "next")
                {
                    continue;
                }

                if (next is not null)
                {
                    throw SearchsetJson.Incomplete("the searchset holds more than one next link");
                }

                next = SearchsetJson.StringMember(link, "url") is { } url
                    ? NativeContinuation.Create(url, baseAddress)
                    : throw SearchsetJson.Incomplete("a next link has no URL");
            }

            return new ContinuationPage(rows, next);
        }
    }
}
