using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Core.Search;

/// <summary>One search parameter a shard advertises for a resource type, as its CapabilityStatement lists it (design spec §9.1).</summary>
/// <remarks>
/// <see cref="SearchCapabilities"/> admits the parameter only when it means the R4 parameter of that code: the type must
/// match, and a supplied <see cref="Definition"/> must be the R4 canonical URL. Without a definition, code and type decide.
/// </remarks>
public sealed record SearchParameterSupport
{
    /// <summary>Initializes a new instance of the <see cref="SearchParameterSupport"/> class.</summary>
    /// <param name="code">The search code, such as <c>name</c> or <c>_id</c>.</param>
    /// <param name="type">The advertised parameter type.</param>
    /// <param name="definition">The advertised definition canonical, or <see langword="null"/> when the shard lists none.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is not a search code.</exception>
    public SearchParameterSupport(string code, SearchParamType type, Uri? definition)
    {
        if (!R4Search.IsSearchCode(code))
        {
            throw new ArgumentException("A search code is a letter or '_' followed by letters, digits, '_' or '-'.", nameof(code));
        }

        Code = code;
        Type = type;
        Definition = definition;
    }

    /// <summary>Gets the search code, compared ordinally.</summary>
    public string Code { get; }

    /// <summary>Gets the parameter type.</summary>
    public SearchParamType Type { get; }

    /// <summary>Gets the definition canonical, or <see langword="null"/> when none was advertised.</summary>
    public Uri? Definition { get; }
}
