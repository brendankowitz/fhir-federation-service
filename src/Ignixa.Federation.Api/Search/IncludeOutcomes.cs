using System.Text.Json;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The fixed informational outcomes a searchset page carries when its reverse includes are partial. The text names no
/// resource type, identifier, query value or backend.
/// </summary>
internal static class IncludeOutcomes
{
    /// <summary>The outcome when the caller may not read some requested include source type.</summary>
    public static ReadOnlyMemory<byte> Suppressed { get; } =
        Warning("suppressed", "Some included resources were omitted because the caller may not read their type.");

    /// <summary>The outcome when more eligible includes exist than one page may carry.</summary>
    public static ReadOnlyMemory<byte> Incomplete { get; } =
        Warning("incomplete", "Included resources were truncated at the configured limit.");

    private static byte[] Warning(string code, string diagnostics)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "OperationOutcome");
            writer.WriteStartArray("issue");
            writer.WriteStartObject();
            writer.WriteString("severity", "warning");
            writer.WriteString("code", code);
            writer.WriteString("diagnostics", diagnostics);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
