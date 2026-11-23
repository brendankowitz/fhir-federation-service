using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.References;

/// <summary>A reverse include search cannot be compiled exactly, so the include fails as a whole instead of returning a partial answer.</summary>
/// <remarks>Its message names only the shard and the reason; it never holds query text or resource identifiers.</remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "A refusal is meaningless without its reason and shard: both are required, so no constructor omits them.")]
public sealed class RevIncludeCompilationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RevIncludeCompilationException"/> class.</summary>
    /// <param name="reason">Why the search is refused.</param>
    /// <param name="shard">The shard whose request or evidence caused the refusal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is undefined.</exception>
    public RevIncludeCompilationException(RevIncludeCompilationReason reason, ShardId shard)
        : base(Describe(reason, shard))
    {
        Reason = reason;
        Shard = shard;
    }

    /// <summary>Gets why the search is refused.</summary>
    public RevIncludeCompilationReason Reason { get; }

    /// <summary>Gets the shard whose request or evidence caused the refusal.</summary>
    public ShardId Shard { get; }

    private static string Describe(RevIncludeCompilationReason reason, ShardId shard)
    {
        ArgumentNullException.ThrowIfNull(shard);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Undefined reverse include compilation reason.");
        }

        return $"The reverse include search of shard '{shard}' is refused: {reason}.";
    }
}
