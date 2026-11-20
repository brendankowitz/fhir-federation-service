using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

internal static class JsonExtensions
{
    /// <summary>A property of an object, or an <c>Undefined</c> element if there is no such property.</summary>
    public static JsonElement Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>The string value of a property, or <c>null</c> if it is absent or not a string.</summary>
    public static string? Str(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>The elements of an array property; empty if it is absent or not an array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string name)
    {
        var value = element.Child(name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    }
}
