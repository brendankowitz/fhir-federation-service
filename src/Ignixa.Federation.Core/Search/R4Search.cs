using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Ignixa.Abstractions;
using Ignixa.Federation.Core.Identity;
using Ignixa.Search.Definition;
using Ignixa.Search.Exceptions;
using Ignixa.Search.Expressions;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.Models;
using Ignixa.Search.Parsing;
using Ignixa.Serialization.Abstractions;
using Ignixa.Specification.Generated;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// The published R4 search definitions and the <c>Ignixa.Search</c> compiler, used only through public in-memory APIs
/// (design spec §6.1).
/// </summary>
/// <remarks>
/// The schema and definitions are built once and only read afterwards. The compiler is built per call because its
/// thread-safety is not documented and it is cheap next to a federated search.
/// </remarks>
internal static partial class R4Search
{
    /// <summary>The projected syntax kind of a plain parameter key, and of a chain's terminal.</summary>
    public const string ParameterKey = "ParameterKey";

    /// <summary>The projected syntax kind of a forward chain key.</summary>
    public const string ForwardChain = "ForwardChain";

    /// <summary>The projected syntax kind of a <c>_has</c> chain key.</summary>
    public const string ReverseChain = "ReverseChain";

    private const string Alternatives = "Alternatives";

    private static readonly Lazy<R4CoreSchemaProvider> Schema = new(() => new R4CoreSchemaProvider());

    private static readonly Lazy<SearchParameterDefinitionManager> Definitions =
        new(() => new SearchParameterDefinitionManager(Schema.Value, NullLogger<SearchParameterDefinitionManager>.Instance));

    // The schema's own name set ignores case; resource types do not.
    private static readonly Lazy<FrozenSet<string>> ResourceNames =
        new(() => Schema.Value.ResourceTypeNames.ToFrozenSet(StringComparer.Ordinal));

    /// <summary>Gets a value indicating whether <paramref name="code"/> is spelled as a search parameter code.</summary>
    public static bool IsSearchCode(string? code) => code is not null && SearchCodePattern().IsMatch(code);

    /// <summary>Gets a value indicating whether <paramref name="name"/> is an R4 resource type, compared ordinally.</summary>
    public static bool IsResourceType(string name) => ResourceNames.Value.Contains(name);

    /// <summary>Finds the R4 definition of <paramref name="code"/> on <paramref name="type"/>, matching the code exactly.</summary>
    public static SearchParameterInfo? FindDefinition(ResourceType type, string code) =>
        Definitions.Value.TryGetSearchParameter(type.Value, code, out SearchParameterInfo? definition) && definition.Code == code
            ? definition
            : null;

    /// <summary>
    /// Compiles one decoded <c>key=value</c> pair against <paramref name="type"/> and returns its trace only when that very
    /// pair compiled.
    /// </summary>
    /// <param name="type">The resource type the pair is applied to.</param>
    /// <param name="key">The decoded key.</param>
    /// <param name="value">The decoded, non-empty value.</param>
    /// <param name="notCompiled">The failure to report when the pair is well formed but not compiled.</param>
    /// <param name="failure">Why the pair has no compiled trace.</param>
    /// <returns>The compiled trace, or <see langword="null"/> with <paramref name="failure"/> set.</returns>
    public static ParameterTrace? Compile(ResourceType type, string key, string value, SearchFailureCode notCompiled, out SearchFailureCode failure)
    {
        R4CoreSchemaProvider schema = Schema.Value;
        SearchParameterDefinitionManager definitions = Definitions.Value;
        var references = new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance);
        var parser = new ExpressionParser(() => definitions, new SearchParameterExpressionParser(references, schema), schema);
        var traces = new List<ParameterTrace>(1);
        failure = notCompiled;
        try
        {
            new SearchOptionsBuilder(parser, definitions).Build(type.Value, [new QueryParameter(key, value)], schema, traces);
        }
        catch (FhirException e) when (e is SearchParameterNotSupportedException or SearchOperationNotSupportedException
            or SearchModifierNotSupportedException or SearchResourceNotSupportedException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            // Observed for a bare `_has` key: the compiler does not understand the key, which is not a value error.
            return null;
        }
        catch (FhirException)
        {
            failure = SearchFailureCode.InvalidValue;
            return null;
        }
        catch (ArgumentException)
        {
            failure = SearchFailureCode.InvalidValue;
            return null;
        }

        if (traces is not [{ Ordinal: 0 } trace] || trace.Key != key || trace.Value != value)
        {
            return null;
        }

        if (trace.Outcome is ParameterOutcome.Failed)
        {
            failure = SearchFailureCode.InvalidValue;
            return null;
        }

        return trace.Outcome is ParameterOutcome.Compiled ? trace : null;
    }

    /// <summary>
    /// Reads how the compiler parsed the trace's decoded key, from its public syntax projection.
    /// </summary>
    /// <param name="trace">A compiled trace whose <see cref="ParameterTrace.Key"/> is the decoded key.</param>
    /// <param name="kind">
    /// <see cref="ParameterKey"/>, <see cref="ForwardChain"/> or <see cref="ReverseChain"/>.
    /// </param>
    /// <param name="terminalStart">
    /// Where the one parameter key starts: 0 for a plain parameter; just after the hop separator for a chain, whose terminal
    /// then runs to the end of the key.
    /// </param>
    /// <returns>Whether the syntax accounts for the whole key as exactly one of those forms.</returns>
    public static bool TryReadKey(ParameterTrace trace, out string kind, out int terminalStart)
    {
        kind = string.Empty;
        terminalStart = 0;
        int length = trace.Key.Length;
        SyntaxNode? syntax = trace.KeySyntax;
        if (syntax is null || !Covers(syntax, SourceOrigin.Key, 0, length))
        {
            return false;
        }

        switch (syntax)
        {
            case { Kind: ParameterKey, Children: [] }:
                kind = ParameterKey;
                return true;
            case { Kind: ForwardChain or ReverseChain, Children: [{ Kind: ParameterKey, Children: [] } terminal] }
                when terminal.Span is { Origin: SourceOrigin.Key, Start: > 0 } span && span.Length > 0 && span.Start + span.Length == length:
                kind = syntax.Kind;
                terminalStart = span.Start;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the compiler split the trace's decoded value into exactly
    /// <paramref name="alternatives"/>, from its public syntax projection.
    /// </summary>
    /// <remarks>
    /// The compiler may read a value as one atom where FHIR reads OR alternatives, as <c>Ignixa.Search</c> 0.6.82 does for
    /// <c>:text</c>; such a value would be forwarded with a meaning nobody compiled.
    /// </remarks>
    public static bool ReadsAlternatives(ParameterTrace trace, IReadOnlyList<(int Start, int Length)> alternatives)
    {
        SyntaxNode? syntax = trace.ValueSyntax;
        if (syntax is null || !Covers(syntax, SourceOrigin.Value, 0, trace.Value.Length))
        {
            return false;
        }

        IReadOnlyList<SyntaxNode> read = syntax.Kind == Alternatives ? syntax.Children : [syntax];
        return read.Count == alternatives.Count
            && read.Zip(alternatives).All(p => Covers(p.First, SourceOrigin.Value, p.Second.Start, p.Second.Length));
    }

    /// <summary>
    /// Gets a value indicating whether <paramref name="definition"/> selects only <c>Reference</c> elements of
    /// <paramref name="source"/>: never a <c>canonical</c>, and never an expression this check cannot read.
    /// </summary>
    /// <remarks>
    /// Walks each union term of the R4 FHIRPath expression that starts at <paramref name="source"/>, after removing a
    /// trailing <c>.where(resolve() is X)</c> or <c>as Reference</c>, through the published schema. Conservative: an
    /// unreadable term is not plain.
    /// </remarks>
    public static bool IsPlainReference(SearchParameterInfo definition, ResourceType source)
    {
        string prefix = source.Value + ".";
        string[] terms =
        [
            .. definition.Expression.Split('|')
                .Select(t => t.Trim())
                .Select(t => t.StartsWith('(', StringComparison.Ordinal) && t.EndsWith(')', StringComparison.Ordinal) ? t[1..^1].Trim() : t)
                .Where(t => t.StartsWith(prefix, StringComparison.Ordinal)),
        ];
        return terms.Length > 0 && terms.All(t => ReferenceTerm().Match(t) is { Success: true } m && IsReferenceElement(m.Groups["path"].Value));
    }

    private static bool Covers(SyntaxNode node, SourceOrigin origin, int start, int length) =>
        node.Span.Origin == origin && node.Span.Start == start && node.Span.Length == length;

    private static bool IsReferenceElement(string path)
    {
        string[] steps = path.Split('.');
        string owner = steps[0];
        for (int i = 1; i < steps.Length; i++)
        {
            ITypeExtended? element = Schema.Value.GetTypeDefinition(owner)?.Children
                .OfType<ITypeExtended>()
                .FirstOrDefault(c => c.Info.Name == steps[i]);
            if (element is null)
            {
                return false;
            }

            string[] codes = [.. element.Types.Select(t => t.Code)];
            if (i == steps.Length - 1)
            {
                return codes.Contains("Reference", StringComparer.Ordinal) && !codes.Contains("canonical", StringComparer.Ordinal);
            }

            if (codes is not [string code])
            {
                return false;
            }

            // Backbone elements are defined under their path; other complex elements under their type.
            owner = code is "BackboneElement" or "Element" ? $"{owner}.{steps[i]}" : code;
        }

        return false;
    }

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SearchCodePattern();

    [GeneratedRegex(
        @"\A(?<path>[A-Z][A-Za-z]*(?:\.[a-z][A-Za-z]*)+)(?:\.where\(resolve\(\) is [A-Z][A-Za-z]*\)| as Reference|\.as\(Reference\))?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceTerm();
}
