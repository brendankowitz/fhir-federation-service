using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Core.Search;

/// <summary>One search parameter a shard advertises for a resource type, as its CapabilityStatement lists it (design spec §9.1).</summary>
/// <remarks>
/// <see cref="SearchCapabilities"/> admits the parameter only when it means the R4 parameter of that code: the type must
/// match, and a supplied <see cref="Definition"/> must equal the packaged R4 canonical ordinally (version and fragment
/// included, no URI normalization). Without a definition, code and type decide.
/// </remarks>
public sealed record SearchParameterSupport
{
    /// <summary>Initializes a new instance of the <see cref="SearchParameterSupport"/> class.</summary>
    /// <param name="code">The search code, such as <c>name</c> or <c>_id</c>.</param>
    /// <param name="type">The advertised parameter type.</param>
    /// <param name="definition">
    /// The advertised definition canonical, kept exactly as given, or <see langword="null"/> when the shard lists none.
    /// Any other value must follow the FHIR R4 <c>canonical</c> grammar (<c>\S*</c>): non-empty, with no whitespace or
    /// control characters. It need not be an absolute URI; a canonical is an identifier, never dereferenced.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not a search code, or <paramref name="definition"/> is empty or contains whitespace or control characters.
    /// </exception>
    public SearchParameterSupport(string code, SearchParamType type, string? definition)
    {
        if (!R4Search.IsSearchCode(code))
        {
            throw new ArgumentException("A search code is a letter or '_' followed by letters, digits, '_' or '-'.", nameof(code));
        }

        if (definition is not null && (definition.Length == 0 || definition.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new ArgumentException("A definition canonical is non-empty and has no whitespace or control characters.", nameof(definition));
        }

        Code = code;
        Type = type;
        Definition = definition;
    }

    /// <summary>Gets the search code, compared ordinally.</summary>
    public string Code { get; }

    /// <summary>Gets the parameter type.</summary>
    public SearchParamType Type { get; }

    /// <summary>Gets the definition canonical exactly as advertised, or <see langword="null"/> when none was advertised.</summary>
    public string? Definition { get; }
}
