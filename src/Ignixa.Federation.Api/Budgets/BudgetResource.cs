using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Budgets;

/// <summary>The request-wide allowance a <see cref="RequestBudget"/> found exhausted (design spec §7.8).</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The integration test project exercises this type; the host project has no InternalsVisibleTo.")]
public enum BudgetResource
{
    /// <summary>The backend-call allowance: <c>BackendCallsPerShard</c> times the shard count.</summary>
    BackendCalls,

    /// <summary>The response-body byte allowance: <c>MaxResponseBytes</c>.</summary>
    ResponseBytes,
}
