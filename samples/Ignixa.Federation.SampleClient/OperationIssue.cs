using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

/// <summary>One OperationOutcome issue: either why a request failed, or a search "outcome" entry.</summary>
public sealed record OperationIssue(string Severity, string Code, string? Diagnostics)
{
    private const int MaxDiagnosticsLength = 200;

    internal static List<OperationIssue> ReadAll(JsonElement outcome) =>
        [.. outcome.Items("issue").Select(issue => new OperationIssue(
            issue.Str("severity") ?? "unknown",
            issue.Str("code") ?? "unknown",
            issue.Str("diagnostics") is { } text ? text[..Math.Min(text.Length, MaxDiagnosticsLength)] : null))];
}
