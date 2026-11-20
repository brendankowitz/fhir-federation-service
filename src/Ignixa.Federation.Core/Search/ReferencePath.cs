using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// One typed reference hop: a source resource type, its reference search code, and the actual target type
/// (design spec §7.5). The unit chain modes, certifications and includes are decided for.
/// </summary>
public sealed record ReferencePath
{
    /// <summary>Initializes a new instance of the <see cref="ReferencePath"/> class.</summary>
    /// <param name="source">The resource type holding the reference.</param>
    /// <param name="code">The reference search code on <paramref name="source"/>, such as <c>subject</c>.</param>
    /// <param name="target">The resource type the reference points at.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is not a search code.</exception>
    public ReferencePath(ResourceType source, string code, ResourceType target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!R4Search.IsSearchCode(code))
        {
            throw new ArgumentException("A search code is a letter or '_' followed by letters, digits, '_' or '-'.", nameof(code));
        }

        Source = source;
        Code = code;
        Target = target;
    }

    /// <summary>Gets the resource type holding the reference.</summary>
    public ResourceType Source { get; }

    /// <summary>Gets the reference search code.</summary>
    public string Code { get; }

    /// <summary>Gets the target resource type.</summary>
    public ResourceType Target { get; }

    /// <summary>Formats the path as <c>Source.code:Target</c>, the spelling <c>Federation:ChainResolution:Passthrough</c> lists.</summary>
    /// <returns>The path text.</returns>
    public override string ToString() => $"{Source.Value}.{Code}:{Target.Value}";
}
