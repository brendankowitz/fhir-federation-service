namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>Whether a case is a requirement the federation depends on or a description of backend behavior.</summary>
public enum EvidenceLabel
{
    /// <summary>The federation's reference planning relies on this behavior (design §4.1, §7.5, §7.6).</summary>
    Contract,

    /// <summary>Documents what the backend does. Another outcome is a finding, not a federation defect.</summary>
    Characterization,
}

/// <summary>
/// Compares the expected match ids to the actual match ids as sorted multisets and describes the
/// difference. The description is bounded and echoes only reserved synthetic ids.
/// </summary>
public sealed class MatchComparison
{
    private const int MaxListedIds = 8;

    private readonly EvidenceLabel _label;
    private readonly string _query;
    private readonly string[] _expected;
    private readonly string[] _actual;
    private readonly string[] _missing;
    private readonly string[] _unexpected;

    private MatchComparison(EvidenceLabel label, string query, string[] expected, string[] actual)
    {
        _label = label;
        _query = query;
        _expected = expected;
        _actual = actual;
        _missing = MultisetExcept(expected, actual);
        _unexpected = MultisetExcept(actual, expected);
    }

    public bool IsEqual => _missing.Length == 0 && _unexpected.Length == 0;

    internal static MatchComparison Compare(
        EvidenceLabel label,
        string queryDescription,
        IReadOnlyCollection<string> expected,
        SearchsetEvidence actual) =>
        new(
            label,
            queryDescription,
            [.. expected.Order(StringComparer.Ordinal)],
            [.. actual.MatchIds]);

    public string Describe()
    {
        string tag = _label == EvidenceLabel.Contract ? "[contract]" : "[characterization]";
        string verdict = IsEqual ? "matched" : _label == EvidenceLabel.Contract ? "unsupported match behavior" : "different behavior";
        return $"{tag} {_query}: {verdict}. expected: {Format(_expected)}; actual: {Format(_actual)}; "
            + $"missing: {Format(_missing)}; unexpected: {Format(_unexpected)}";
    }

    private static string[] MultisetExcept(string[] from, string[] remove)
    {
        var remaining = remove.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string id in from)
        {
            if (remaining.TryGetValue(id, out int count) && count > 0)
            {
                remaining[id] = count - 1;
            }
            else
            {
                result.Add(id);
            }
        }

        return [.. result];
    }

    private static string Format(string[] ids)
    {
        IEnumerable<string> shown = ids.Take(MaxListedIds).Select(id => SeedData.IsReservedObservationId(id) ? id : "<unreserved id>");
        string extra = ids.Length > MaxListedIds ? $", +{ids.Length - MaxListedIds} more" : string.Empty;
        return $"[{string.Join(", ", shown)}{extra}]";
    }
}
