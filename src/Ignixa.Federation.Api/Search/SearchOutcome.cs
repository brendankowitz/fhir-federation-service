using System.Text.Json;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// Federation <c>OperationOutcome</c> error responses for the search surface.
/// </summary>
/// <remarks>
/// Diagnostics are fixed text chosen by the host. Nothing from the request, a backend or an exception is ever
/// written, except a shard alias, which is trusted configuration.
/// </remarks>
internal static class SearchOutcome
{
    private const string ContentType = "application/fhir+json; charset=utf-8";

    public static IResult Invalid(string diagnostics) => Error(StatusCodes.Status400BadRequest, "invalid", diagnostics);

    public static IResult NotSupported(int statusCode, string diagnostics) => Error(statusCode, "not-supported", diagnostics);

    public static IResult TooCostly() =>
        Error(StatusCodes.Status400BadRequest, "too-costly", "The search needs more backend work than one request may use.");

    public static IResult Gone() =>
        Error(StatusCodes.Status410Gone, "not-found", "The continuation is no longer valid; repeat the original search.");

    public static IResult Unavailable(string diagnostics) => Error(StatusCodes.Status503ServiceUnavailable, "transient", diagnostics);

    /// <summary>Builds an error response.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="code">The FHIR issue type code.</param>
    /// <param name="diagnostics">Fixed, host-chosen text.</param>
    /// <returns>The response.</returns>
    public static IResult Error(int statusCode, string code, string diagnostics)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "OperationOutcome");
            writer.WriteStartArray("issue");
            writer.WriteStartObject();
            writer.WriteString("severity", "error");
            writer.WriteString("code", code);
            writer.WriteString("diagnostics", diagnostics);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Results.Text(buffer.ToArray(), ContentType, statusCode);
    }
}
