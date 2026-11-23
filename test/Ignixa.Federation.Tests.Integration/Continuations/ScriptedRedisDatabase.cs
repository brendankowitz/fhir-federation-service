using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>One call the adapter made to the scripted <see cref="IDatabase"/>.</summary>
internal sealed record RedisCall(string Method, IReadOnlyList<RedisKey> Keys, CommandFlags Flags);

/// <summary>
/// A scripted <see cref="IDatabase"/> for deterministic fault control at the provider boundary: every call is recorded
/// and answered by <see cref="Respond"/>, which returns the reply (a <see cref="RedisResult"/> built as the pinned client
/// types a RESP2 or RESP3 reply), a task of it (to hold or fault an in-flight call), or throws (a synchronous client
/// failure). It runs no Lua and is not
/// Redis: it is evidence about the adapter's reply and failure handling only. Real semantics need the interop tests.
/// </summary>
[SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires a non-sealed proxy type.")]
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "DispatchProxy.Create instantiates it by reflection.")]
internal class ScriptedRedisDatabase : DispatchProxy
{
    private readonly List<RedisCall> _calls = [];
    private readonly Lock _lock = new();

    public Func<RedisCall, object> Respond { get; set; } = _ => throw new InvalidOperationException("No reply scripted.");

    public IReadOnlyList<RedisCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public static (IDatabase Database, ScriptedRedisDatabase Script) Create(Func<RedisCall, object>? respond = null)
    {
        IDatabase database = Create<IDatabase, ScriptedRedisDatabase>();
        var script = (ScriptedRedisDatabase)(object)database;
        if (respond is not null)
        {
            script.Respond = respond;
        }

        return (database, script);
    }

    public static RedisResult Integer(long value) => RedisResult.Create(value, ResultType.Integer);

    public static RedisResult Bulk(byte[] value) => RedisResult.Create(value, ResultType.BulkString);

    // The client gives a RESP2 null bulk string ($-1) and a RESP3 null (_) the same shape: Resp3Type Null, Resp2Type
    // BulkString. A null array (*-1) is Resp3Type Null, Resp2Type Array.
    public static RedisResult NullBulk() => RedisResult.Create(RedisValue.Null, ResultType.BulkString);

    public static RedisResult Resp3Null() => RedisResult.Create(RedisValue.Null, ResultType.Null);

    public static RedisResult NullArray() => RedisResult.Create((RedisResult[])null!);

    public static RedisResult Boolean(bool value) => RedisResult.Create(value ? 1 : 0, ResultType.Boolean);

    public static RedisResult Verbatim(string value) => RedisResult.Create(value, ResultType.VerbatimString);

    public static RedisResult Simple(string value) => RedisResult.Create(value, ResultType.SimpleString);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        RedisCall call = targetMethod.Name switch
        {
            nameof(IDatabase.ScriptEvaluate) or nameof(IDatabase.ScriptEvaluateAsync) when args is [string, RedisKey[] keys, RedisValue[], CommandFlags flags] =>
                new RedisCall(targetMethod.Name, keys, flags),
            // Only a raw one-key DEL, whose reply the adapter interprets itself; the client's KeyDelete decoder coerces.
            nameof(IDatabase.Execute) or nameof(IDatabase.ExecuteAsync)
                when args is ["DEL", ICollection<object> commandArgs, CommandFlags flags] && commandArgs.All(a => a is RedisKey) =>
                new RedisCall($"{targetMethod.Name} DEL", [.. commandArgs.Cast<RedisKey>()], flags),
            _ => throw new NotSupportedException("The adapter called an unscripted IDatabase member."),
        };

        lock (_lock)
        {
            _calls.Add(call);
        }

        object reply = Respond(call);
        Type returnType = targetMethod.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return reply is Task task ? Unwrap(task) : reply;
        }

        Type resultType = returnType.GetGenericArguments()[0];
        Task source = reply as Task ?? Task.FromResult(reply);
        return typeof(ScriptedRedisDatabase).GetMethod(nameof(Cast), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType).Invoke(null, [source]);
    }

    private static object Unwrap(Task task)
    {
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task)!;
    }

    private static async Task<T> Cast<T>(Task source)
    {
        await source.ConfigureAwait(false);
        return (T)source.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(source)!;
    }
}
