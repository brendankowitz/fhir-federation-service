using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Shards.Credentials;

/// <summary>Acquiring a shard credential failed (design spec §10.2).</summary>
/// <remarks>
/// <para>The message is fixed text chosen from <see cref="Failure"/> alone. There is deliberately no constructor that
/// takes a message or an inner exception, so no secret, token, tenant, client id, secret name or SDK error text can
/// reach a log through this type.</para>
/// <para>An <see cref="IShardSecretResolver"/> throws it to report a secret it cannot supply.</para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "A message or inner-exception constructor would admit secret-bearing text; the message is fixed per failure.")]
public sealed class ShardCredentialException : Exception
{
    /// <summary>Creates the failure with its fixed message.</summary>
    /// <param name="failure">Why the credential could not be acquired.</param>
    public ShardCredentialException(ShardCredentialFailure failure)
        : base(Describe(failure))
    {
        Failure = failure;
    }

    /// <summary>Gets why the credential could not be acquired.</summary>
    public ShardCredentialFailure Failure { get; }

    private static string Describe(ShardCredentialFailure failure) => failure switch
    {
        ShardCredentialFailure.InvalidConfiguration => "The shard credential settings are not usable.",
        ShardCredentialFailure.SecretUnavailable => "The shard client secret could not be resolved.",
        ShardCredentialFailure.TokenUnavailable => "The identity platform did not issue a shard access token.",
        ShardCredentialFailure.InvalidToken => "The identity platform returned an unusable shard access token.",
        ShardCredentialFailure.TimedOut => "Acquiring the shard credential was canceled before it completed.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), "Unknown credential failure."),
    };
}
