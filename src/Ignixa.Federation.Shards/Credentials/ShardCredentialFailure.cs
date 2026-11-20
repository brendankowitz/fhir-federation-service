namespace Ignixa.Federation.Shards.Credentials;

/// <summary>Why acquiring a shard credential failed; the only detail a <see cref="ShardCredentialException"/> carries.</summary>
public enum ShardCredentialFailure
{
    /// <summary>The shard's authentication settings cannot produce a token request, for example a malformed audience.</summary>
    InvalidConfiguration,

    /// <summary>The client secret could not be resolved, was missing, or was empty.</summary>
    SecretUnavailable,

    /// <summary>The identity platform refused or could not issue a token.</summary>
    TokenUnavailable,

    /// <summary>The identity platform answered, but not with usable bearer text.</summary>
    InvalidToken,

    /// <summary>The secret store or identity platform was canceled by something other than the caller's token, such as an SDK timeout.</summary>
    TimedOut,
}
