namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>A request exactly as it arrived at <see cref="LoopbackFhirServer"/>, before any server-side decoding.</summary>
/// <param name="Method">The request-line method.</param>
/// <param name="Target">The request-line target, byte for byte.</param>
/// <param name="Headers">The header fields in arrival order, names as sent.</param>
/// <param name="Body">The body bytes.</param>
/// <param name="Connection">The 1-based number of the connection it arrived on, in accept order.</param>
internal sealed record RecordedRequest(string Method, string Target, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body, int Connection)
{
    /// <summary>Gets every value sent for <paramref name="name"/>, compared case-insensitively.</summary>
    public IReadOnlyList<string> HeaderValues(string name) =>
        [.. Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value)];
}
