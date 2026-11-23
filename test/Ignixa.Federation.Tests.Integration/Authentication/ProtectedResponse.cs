using System.Net;
using System.Text.Json;

namespace Ignixa.Federation.Tests.Integration.Authentication;

/// <summary>What a caller observed from the protected endpoint.</summary>
/// <param name="StatusCode">The HTTP status code.</param>
/// <param name="Challenges">The <c>WWW-Authenticate</c> header values.</param>
/// <param name="Body">The parsed JSON body, or <see langword="null"/> when the response had none.</param>
internal sealed record ProtectedResponse(HttpStatusCode StatusCode, IReadOnlyList<string> Challenges, JsonElement? Body)
{
    /// <summary>Gets the values of every claim of the exact <paramref name="type"/> the endpoint saw.</summary>
    public string[] ClaimValues(string type) =>
    [
        .. Body!.Value.GetProperty("claims").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == type)
            .Select(c => c.GetProperty("value").GetString()!),
    ];
}
