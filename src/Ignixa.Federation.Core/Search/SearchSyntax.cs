using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Ignixa.Federation.Core.Identity;
using Ignixa.Search.Expressions;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Parsing;

namespace Ignixa.Federation.Core.Search;

/// <summary>Release-independent lexical and compiler trace alignment rules.</summary>
internal static partial class SearchSyntax
{
    public const string ParameterKey = "ParameterKey";
    public const string ForwardChain = "ForwardChain";
    public const string ReverseChain = "ReverseChain";
    private const string Alternatives = "Alternatives";

    public static bool IsSearchCode(string? code) => code is not null && SearchCodePattern().IsMatch(code);

    public static bool TryReadReferencePath(string term, [NotNullWhen(true)] out string? path)
    {
        Match match = ReferenceTerm().Match(term);
        path = match.Success ? match.Groups["path"].Value : null;
        return match.Success;
    }

    public static bool IsReferenceValue(string alternative, IReadOnlySet<string> resourceNames)
    {
        string value = Unescape().Replace(alternative, "$1");
        if (value.Any(c => c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        if (ResourceId.TryParse(value, out _))
        {
            return true;
        }

        int bar = value.LastIndexOf('|', StringComparison.Ordinal);
        string uri = bar < 0 ? value : value[..bar];
        if (UriScheme().Match(uri) is { Success: true } scheme && bar != value.Length - 1)
        {
            bool web = scheme.Groups["scheme"].Value.ToUpperInvariant() is "HTTP" or "HTTPS";
            return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
                && (!web || (scheme.Groups["rest"].Value.StartsWith("//", StringComparison.Ordinal) && parsed.Host.Length > 0));
        }

        return value.Split('/') switch
        {
            [string type, string id] => resourceNames.Contains(type) && ResourceId.TryParse(id, out _),
            [string type, string id, "_history", string version] =>
                resourceNames.Contains(type) && ResourceId.TryParse(id, out _) && ResourceId.TryParse(version, out _),
            _ => false,
        };
    }

    public static ParameterTrace? CompiledTrace(List<ParameterTrace> traces, string key, string value, ref SearchFailureCode failure)
    {
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

    private static bool Covers(SyntaxNode node, SourceOrigin origin, int start, int length) =>
        node.Span.Origin == origin && node.Span.Start == start && node.Span.Length == length;

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SearchCodePattern();

    [GeneratedRegex(@"\A(?<path>[A-Z][A-Za-z]*(?:\.[a-z][A-Za-z]*)+)(?:\.where\(resolve\(\) is [A-Z][A-Za-z]*\)| as Reference|\.as\(Reference\))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceTerm();

    [GeneratedRegex(@"\\(.)", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Unescape();

    [GeneratedRegex(@"\A(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*):(?<rest>.+)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex UriScheme();
}
