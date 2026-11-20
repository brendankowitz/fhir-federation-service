using Ignixa.Federation.Shards.Credentials;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>A secret store whose answers and failures a test scripts, recording every name it was asked for.</summary>
internal sealed class ScriptedSecretResolver : IShardSecretResolver
{
    private readonly List<string> _requested = [];

    /// <summary>Gets or sets what the next lookups return or do; by default they return <see cref="Value"/>.</summary>
    public Func<string, CancellationToken, ValueTask<string>> Lookup { get; set; }

    public ScriptedSecretResolver(string value)
    {
        Value = value;
        Lookup = (_, _) => ValueTask.FromResult(Value);
    }

    /// <summary>Gets or sets the current stored secret, as a rotation would change it.</summary>
    public string Value { get; set; }

    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_requested)
            {
                return [.. _requested];
            }
        }
    }

    public ValueTask<string> ResolveAsync(string secretName, CancellationToken cancellationToken)
    {
        lock (_requested)
        {
            _requested.Add(secretName);
        }

        return Lookup(secretName, cancellationToken);
    }
}
