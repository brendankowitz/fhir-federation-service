using System.Net;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// The federation answered 410 Gone for a continuation. The only recovery is a new search from the original query;
/// the client never restarts one on its own.
/// </summary>
public sealed class SearchExpiredException(IReadOnlyList<OperationIssue> issues)
    : FederationRequestException(
        HttpStatusCode.Gone, issues, "The search continuation has expired. Restart the search from the original query.");
