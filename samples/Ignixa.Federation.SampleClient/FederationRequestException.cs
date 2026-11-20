using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// A non-success response. The message carries only the status; details are in <see cref="Issues"/>. The raw body,
/// request URL (which may hold an opaque continuation token) and headers are never included.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Only this client throws it, always with its required data; the standard constructors would build an instance without it.")]
internal class FederationRequestException : Exception
{
    public FederationRequestException(HttpStatusCode statusCode, IReadOnlyList<OperationIssue> issues)
        : this(statusCode, issues, Describe(statusCode))
    {
    }

    protected FederationRequestException(HttpStatusCode statusCode, IReadOnlyList<OperationIssue> issues, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Issues = issues;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Issues from the response's OperationOutcome; empty if the body was not one.</summary>
    public IReadOnlyList<OperationIssue> Issues { get; }

    private static string Describe(HttpStatusCode status) =>
        $"The request failed with HTTP {(int)status} ({status})."
        + ((int)status is >= 300 and < 400 ? " Redirects are never followed." : "");
}
