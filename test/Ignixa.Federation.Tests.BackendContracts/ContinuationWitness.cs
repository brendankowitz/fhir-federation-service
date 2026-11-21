namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>The outcome of one directional walk. <see cref="Describe"/> prints only reserved values and counts.</summary>
internal sealed record ContinuationReport(bool Matched, string Summary)
{
    public string Describe(string label) => $"[contract] {label}: {(Matched ? "matched" : "unsupported continuation behavior")}. {Summary}";
}

/// <summary>
/// Walks a sorted Patient search through its native continuations against an independent direct-read oracle.
/// The expected order comes from the update instants of direct reads, never from a search, and every issued
/// link is redeemed twice, because a continuation that works once but not on retry is not a stable cursor.
/// </summary>
internal static class ContinuationWitness
{
    private const int RedemptionsPerLink = 2;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Sends one start search, then at most two redemptions of each distinct link per page level, which is
    /// 15 searches for three Patients, after one direct read per reserved Patient. A mismatch is a failed
    /// <see cref="ContinuationReport"/>; a response that cannot be accounted for, a tie between oracle
    /// instants, or an exhausted time budget is a <see cref="BackendEvidenceException"/>.
    /// </summary>
    /// <exception cref="BackendEvidenceException">The walk could not produce trustworthy evidence.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ContinuationReport> RunAsync(
        BackendHttpClient client,
        IReadOnlyList<string> reservedIds,
        HttpMethod method,
        PatientSortDirection direction,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            return await WalkAsync(client, reservedIds, method, direction, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw SearchsetJson.Incomplete("the continuation walk exceeded its time budget");
        }
    }

    private static async Task<ContinuationReport> WalkAsync(
        BackendHttpClient client,
        IReadOnlyList<string> reservedIds,
        HttpMethod method,
        PatientSortDirection direction,
        CancellationToken cancellationToken)
    {
        var oracle = new List<PatientRow>();
        foreach (string id in reservedIds)
        {
            oracle.Add(await client.ReadPatientAsync(id, cancellationToken));
        }

        if (oracle.Select(row => row.Instant).Distinct().Count() != oracle.Count)
        {
            throw SearchsetJson.Incomplete("the reserved Patients' update instants are not distinct, so no sort order is defined");
        }

        PatientRow[] ascending = [.. oracle.OrderBy(row => row.Instant)];
        PatientRow[] expected = direction == PatientSortDirection.Ascending ? ascending : [.. ascending.Reverse()];
        string oracleText = $"Direct reads by update instant: {string.Join("; ", ascending.Select(row => row.Describe()))}.";
        int searches = 1;

        ContinuationPage start = await client.StartSortedPageAsync(method, direction, cancellationToken);
        if (Check(start, 0, "start page", expected) is { } startFailure)
        {
            return new ContinuationReport(false, $"{oracleText} {startFailure}.");
        }

        List<NativeContinuation> links = start.Next is { } first ? [first] : [];
        for (int position = 1; position <= expected.Length && links.Count > 0; position++)
        {
            List<NativeContinuation> successors = [];
            foreach (NativeContinuation link in links)
            {
                for (int redemption = 1; redemption <= RedemptionsPerLink; redemption++)
                {
                    ContinuationPage page = await client.RedeemAsync(method, link, cancellationToken);
                    searches++;
                    if (Check(page, position, $"continuation redemption {redemption} of the link after row {position}", expected) is { } failure)
                    {
                        return new ContinuationReport(false, $"{oracleText} {failure}.");
                    }

                    if (page.Next is { } next && !successors.Any(known => string.Equals(known.Query, next.Query, StringComparison.Ordinal)))
                    {
                        successors.Add(next);
                    }
                }
            }

            links = successors;
        }

        return new ContinuationReport(
            true,
            $"{oracleText} {direction} {method.Method} walk matched {expected.Length} rows over {searches} search requests, every issued link redeemed {RedemptionsPerLink} times.");
    }

    /// <summary>
    /// The fixed-text reason <paramref name="page"/> is not what the oracle expects at the zero-based
    /// <paramref name="position"/>, or <see langword="null"/>. The position after the last row is the
    /// terminal page, which holds nothing and offers nothing.
    /// </summary>
    private static string? Check(ContinuationPage page, int position, string label, PatientRow[] expected)
    {
        if (position == expected.Length)
        {
            return page.Rows.Count > 0 ? $"{label}: an extra row follows the last expected row"
                : page.Next is not null ? $"{label}: a next link follows the terminal page"
                : null;
        }

        int row = position + 1;
        if (page.Rows.Count == 0)
        {
            return $"{label}: row {row} is missing";
        }

        if (page.Rows.Count > 1)
        {
            return $"{label} holds {page.Rows.Count} rows, not one";
        }

        PatientRow actual = page.Rows[0];
        PatientRow wanted = expected[position];
        if (actual.Id != wanted.Id)
        {
            return expected.Take(position).Any(earlier => earlier.Id == actual.Id)
                ? $"{label}: row {row} repeats or moves back to an earlier row"
                : $"{label}: row {row} is not the expected Patient";
        }

        if (actual != wanted)
        {
            return $"{label}: row {row} differs from the direct read in version or instant";
        }

        return page.Next is null && position < expected.Length - 1
            ? $"{label}: the search ended after row {row}"
            : null;
    }
}
