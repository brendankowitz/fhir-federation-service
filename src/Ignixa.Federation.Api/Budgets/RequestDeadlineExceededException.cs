using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Budgets;

/// <summary>
/// The overall request deadline (<c>Federation:Timeouts:Request</c>) passed (design spec §7.8, §8). Only the request
/// deadline raises this; a backend-call or sub-query timeout is not a request expiry. The type carries no HTTP
/// status; the host maps it.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The integration test project exercises this type; the host project has no InternalsVisibleTo.")]
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only RequestBudget creates this exception, always with the configured deadline; a parameterless or message-only form would be an invalid instance.")]
public sealed class RequestDeadlineExceededException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestDeadlineExceededException"/> class.</summary>
    /// <param name="deadline">The configured request deadline.</param>
    public RequestDeadlineExceededException(TimeSpan deadline)
        : base($"The request exceeded its deadline of {deadline}.")
    {
        Deadline = deadline;
    }

    /// <summary>Gets the configured request deadline.</summary>
    public TimeSpan Deadline { get; }
}
