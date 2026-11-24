using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The accounted-for result of one ordinary count request (<c>_count=0</c>): a searchset Bundle with
/// a present, non-negative integer total, no entries, and no continuation. <see cref="Parse"/> is the
/// only way to obtain one, so an instance always means the server answered the count as a count.
/// </summary>
public sealed class CountEvidence
{
    private CountEvidence(int total)
    {
        Total = total;
    }

    public int Total { get; }

    /// <exception cref="BackendEvidenceException">The response is not a complete count searchset.</exception>
    public static CountEvidence Parse(string json)
    {
        using JsonDocument document = SearchsetJson.ParseCompleteSearchset(json, countOnly: true);
        JsonElement root = document.RootElement;

        int entries = SearchsetJson.Array(root, "entry").Count();
        if (entries > 0)
        {
            throw SearchsetJson.Incomplete($"the count response holds {entries} entries, so _count=0 was not honored");
        }

        if (!root.TryGetProperty("total", out JsonElement total)
            || total.ValueKind != JsonValueKind.Number
            || !total.TryGetInt32(out int count)
            || count < 0)
        {
            throw SearchsetJson.Incomplete("the searchset has no non-negative integer total");
        }

        return new CountEvidence(count);
    }

    public bool Matches(int expectedTotal) => Total == expectedTotal;

    /// <summary>
    /// A bounded report of one case. <paramref name="query"/> is a reserved label chosen by the test,
    /// never text from the response.
    /// </summary>
    public string Describe(string query, int expectedTotal) =>
        $"[contract] {query}: {(Matches(expectedTotal) ? "matched" : "unsupported count behavior")}. "
        + $"expected total: {expectedTotal}; actual total: {Total}";
}
