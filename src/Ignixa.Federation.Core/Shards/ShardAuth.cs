using Ignixa.Federation.Core.Configuration;

namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The federation's read-only credential for one shard (design spec §10.2): exactly
/// <see cref="ManagedIdentityAuth"/> or <see cref="ClientCredentialsAuth"/>. There is no anonymous variant.
/// </summary>
/// <remarks>
/// Only this assembly can declare a variant. The parameterless constructor is <see langword="private protected"/>.
/// C# requires the copy constructor of a non-sealed record to be <see langword="protected"/>, so another assembly can
/// derive through it; it therefore throws unless the instance is one of the two variants declared here.
/// </remarks>
public abstract record ShardAuth
{
    private protected ShardAuth()
    {
    }

    /// <summary>Copies a variant; used by <see langword="with"/> on <see cref="ManagedIdentityAuth"/> and <see cref="ClientCredentialsAuth"/>.</summary>
    /// <param name="original">The instance being copied. ShardAuth has no state of its own to copy.</param>
    /// <exception cref="InvalidOperationException">The instance is not a variant declared in this assembly.</exception>
    protected ShardAuth(ShardAuth original)
    {
        if (this is not (ManagedIdentityAuth or ClientCredentialsAuth))
        {
            throw new InvalidOperationException("ShardAuth variants are declared only by Ignixa.Federation.Core.");
        }
    }

    /// <summary>
    /// Validates configured auth. Errors are added to <paramref name="errors"/> under <paramref name="path"/> and never
    /// echo a submitted value: a misplaced secret must not reach a log.
    /// </summary>
    /// <returns>The credential, or <see langword="null"/> when <paramref name="options"/> is invalid.</returns>
    internal static ShardAuth? Parse(AuthOptions? options, string path, ICollection<string> errors)
    {
        if (options is null)
        {
            errors.Add($"{path}: is required.");
            return null;
        }

        switch (options.Type)
        {
            case "ManagedIdentity":
                // Non-short-circuit '&' so every problem is reported.
                bool managedValid = Require(options.Resource, $"{path}:Resource", errors)
                    & Forbid(options.TenantId, $"{path}:TenantId", errors)
                    & Forbid(options.SecretName, $"{path}:SecretName", errors);
                return managedValid
                    ? new ManagedIdentityAuth(options.Resource!, string.IsNullOrWhiteSpace(options.ClientId) ? null : options.ClientId)
                    : null;

            case "ClientCredentials":
                bool credentialsValid = Require(options.TenantId, $"{path}:TenantId", errors)
                    & Require(options.ClientId, $"{path}:ClientId", errors)
                    & Require(options.Resource, $"{path}:Resource", errors)
                    & Require(options.SecretName, $"{path}:SecretName", errors);
                return credentialsValid
                    ? new ClientCredentialsAuth(options.TenantId!, options.ClientId!, options.Resource!, options.SecretName!)
                    : null;

            default:
                errors.Add($"{path}:Type: must be 'ManagedIdentity' or 'ClientCredentials'.");
                return null;
        }
    }

    private static bool Require(string? value, string path, ICollection<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        errors.Add($"{path}: is required.");
        return false;
    }

    private static bool Forbid(string? value, string path, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        errors.Add($"{path}: is not allowed for ManagedIdentity.");
        return false;
    }
}
