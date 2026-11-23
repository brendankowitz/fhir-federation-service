using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Budgets;

/// <summary>
/// A request used more backend calls or response bytes than its <see cref="RequestBudget"/> allows (design spec
/// §7.8). Deterministic: the same request against the same data fails the same way, so this is a client-side
/// <c>too-costly</c> outcome and not a timeout. The type carries no HTTP status; the host maps it.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The integration test project exercises this type; the host project has no InternalsVisibleTo.")]
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only RequestBudget creates this exception, always with the exhausted resource and its limit; a parameterless or message-only form would be an invalid instance.")]
public sealed class RequestTooCostlyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestTooCostlyException"/> class.</summary>
    /// <param name="resource">The allowance that was exhausted.</param>
    /// <param name="limit">The configured allowance.</param>
    public RequestTooCostlyException(BudgetResource resource, long limit)
        : base($"The request exceeded its {resource} budget of {limit}.")
    {
        Resource = resource;
        Limit = limit;
    }

    /// <summary>Gets the allowance that was exhausted.</summary>
    public BudgetResource Resource { get; }

    /// <summary>Gets the configured allowance: calls for <see cref="BudgetResource.BackendCalls"/>, bytes for <see cref="BudgetResource.ResponseBytes"/>.</summary>
    public long Limit { get; }
}
