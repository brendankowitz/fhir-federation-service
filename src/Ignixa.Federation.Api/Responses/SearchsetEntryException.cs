using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Api.Responses;

/// <summary>
/// A shard entry's resource is not a single JSON object in valid UTF-8, so <see cref="SearchsetResponse"/> cannot write
/// the page; the entry's key identifies the shard that returned it.
/// </summary>
/// <remarks>The message names only the entry's position, never the resource bytes.</remarks>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Thrown by the public SearchsetResponse, which the integration test project exercises.")]
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "The entry's key is what callers act on, so no constructor omits it.")]
public sealed class SearchsetEntryException : ArgumentException
{
    /// <summary>Initializes a new instance of the <see cref="SearchsetEntryException"/> class.</summary>
    /// <param name="key">The rejected entry's key.</param>
    /// <param name="message">Why the entry was rejected.</param>
    /// <param name="paramName">The argument that held the entry.</param>
    /// <param name="innerException">The writer's exception, if it threw.</param>
    public SearchsetEntryException(ResourceKey key, string message, string paramName, Exception? innerException)
        : base(message, paramName, innerException)
    {
        Key = key;
    }

    /// <summary>Gets the key of the entry that could not be written.</summary>
    public ResourceKey Key { get; }
}
