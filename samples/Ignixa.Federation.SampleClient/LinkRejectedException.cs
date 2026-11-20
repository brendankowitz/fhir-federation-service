namespace Ignixa.Federation.SampleClient;

/// <summary>
/// A link pointed outside the endpoint allowed to follow it, so no request was sent. The message names the target
/// scheme, host, port and path but never its query (which may hold an opaque continuation token) or user info.
/// </summary>
public sealed class LinkRejectedException(Uri target, string scope)
    : Exception($"Refusing to follow a link to {target.Scheme}://{target.Host}:{target.Port}{target.AbsolutePath}: "
        + $"it is outside {scope}. No request was sent.");
