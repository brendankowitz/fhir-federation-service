using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>A shard call failed in a way the backend or the wire caused (design spec §8).</summary>
/// <remarks>
/// <para>The message is fixed text naming the shard alias, the failure and, for a status failure, the status code. It never
/// holds a URL, query, token, response body, or the message of an exception from the wire, and there is no inner exception,
/// so the exception can be logged without leaking PHI or secrets.</para>
/// <para><see cref="HttpShardClient"/> always sets <see cref="Shard"/> and a classified <see cref="Failure"/>. The standard
/// exception constructors exist for wrappers and test doubles; they leave <see cref="Shard"/> unset and the failure
/// <see cref="ShardCallFailure.Unclassified"/>.</para>
/// </remarks>
public sealed class ShardCallException : Exception
{
    /// <summary>Creates an unclassified shard call failure.</summary>
    public ShardCallException()
    {
    }

    /// <summary>Creates an unclassified shard call failure.</summary>
    /// <param name="message">The message.</param>
    public ShardCallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an unclassified shard call failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ShardCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal ShardCallException(ShardDescriptor shard, ShardCallFailure failure, string reason, int? statusCode = null)
        : base(Describe(shard, failure, reason, statusCode))
    {
        Shard = shard.Id;
        Failure = failure;
        StatusCode = statusCode;
    }

    /// <summary>Gets the shard whose call failed; <see langword="null"/> only for an exception made by a standard constructor.</summary>
    public ShardId? Shard { get; }

    /// <summary>Gets why the call failed.</summary>
    public ShardCallFailure Failure { get; }

    /// <summary>Gets the HTTP status the backend answered with, or <see langword="null"/> when no status decided the failure.</summary>
    public int? StatusCode { get; }

    private static string Describe(ShardDescriptor shard, ShardCallFailure failure, string reason, int? statusCode)
    {
        string status = statusCode is { } code ? $" (HTTP {code})" : string.Empty;
        return $"Shard '{shard.Alias}' call failed: {failure}{status}: {reason}";
    }
}
