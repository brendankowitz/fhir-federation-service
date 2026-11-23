using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// A test-only <see cref="IDatabase"/> over a real interop connection that runs <see cref="Prefix"/> and the adapter's
/// own script as one script, in one execution, so the prefix can stage state at the script's frozen clock. Redis takes
/// one time snapshot per execution unit, and <c>TIME</c> inside it reports that snapshot, so a key the prefix stages to
/// expire at <c>floor(TIME)</c> is still present when the adapter's script runs at that exact millisecond. The prefix
/// sees the script's <c>KEYS</c> and <c>ARGV</c> and may shadow them with locals. Only
/// <see cref="IDatabaseAsync.ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)"/> is supported.
/// </summary>
[SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires a non-sealed proxy type.")]
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "DispatchProxy.Create instantiates it by reflection.")]
internal class ScriptPrefixingDatabase : DispatchProxy
{
    private IDatabase? _inner;

    public string Prefix { get; set; } = string.Empty;

    public static (IDatabase Database, ScriptPrefixingDatabase Staging) Over(IDatabase inner)
    {
        IDatabase database = Create<IDatabase, ScriptPrefixingDatabase>();
        var staging = (ScriptPrefixingDatabase)(object)database;
        staging._inner = inner;
        return (database, staging);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(IDatabase.ScriptEvaluateAsync) && args is [string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags]
            ? _inner!.ScriptEvaluateAsync(Prefix + "\n" + script, keys, values, flags)
            : throw new NotSupportedException("Only string ScriptEvaluateAsync is staged.");
}
