using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// A link pointed outside the endpoint allowed to follow it, so no request was sent. The message names the target
/// scheme, host, port and path but never its query (which may hold an opaque continuation token) or user info.
/// A relative target is not described at all, since its text may be only a path and query.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Only this client throws it, always with its required data; the standard constructors would build an instance without it.")]
internal sealed class LinkRejectedException(Uri target, string scope)
    : Exception((target.IsAbsoluteUri
            ? $"Refusing to follow a link to {target.Scheme}://{target.Host}:{target.Port}{target.AbsolutePath}"
            : "Refusing to follow a relative link")
        + $": it is outside {scope}. No request was sent.");
