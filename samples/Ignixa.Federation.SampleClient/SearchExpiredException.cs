using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// The federation answered 410 Gone for a continuation. The only recovery is a new search from the original query;
/// the client never restarts one on its own.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Only this client throws it, always with its required data; the standard constructors would build an instance without it.")]
internal sealed class SearchExpiredException(IReadOnlyList<OperationIssue> issues)
    : FederationRequestException(
        HttpStatusCode.Gone, issues, "The search continuation has expired. Restart the search from the original query.");
