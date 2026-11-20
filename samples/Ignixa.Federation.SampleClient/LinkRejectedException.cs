namespace Ignixa.Federation.SampleClient;

/// <summary>
/// A link pointed outside the endpoint allowed to follow it, so no request was sent. The message names the target
/// scheme, host, port and path but never its query (which may hold an opaque continuation token) or user info.
/// A relative target is not described at all, since its text may be only a path and query.
/// </summary>
public sealed class LinkRejectedException(Uri target, string scope)
    : Exception((target.IsAbsoluteUri
            ? $"Refusing to follow a link to {target.Scheme}://{target.Host}:{target.Port}{target.AbsolutePath}"
            : "Refusing to follow a relative link")
        + $": it is outside {scope}. No request was sent.");
