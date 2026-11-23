using System.Collections.Frozen;
using System.Globalization;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Search.Models;
using Ignixa.Search.Expressions;
using Ignixa.Search.Parsing;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// Decides, before any backend call, whether every shard can answer a search exactly, and how each occurrence is carried:
/// kept, removed, or replaced by a generated chain terminal (design spec §§6–7).
/// </summary>
/// <remarks>
/// Planning is all-or-nothing. A query is accepted only when the published R4 compiler understood each occurrence and every
/// shard certified each fact the plan relies on; otherwise the first failing occurrence is reported. Failures never echo
/// the query. Immutable; safe to share across threads.
/// </remarks>
public sealed class SearchPlanner
{
    private const string ContinuationKey = "ct";
    private const string Include = "_include";
    private const string RevInclude = "_revinclude";

    private static readonly FrozenSet<string> Controls = FrozenSet.Create(
        StringComparer.Ordinal,
        "_count", "_summary", "_total", "_sort", "_format", "_elements", Include, RevInclude, "_contained", "_containedType",
        "_score", "_pretty", "_graph", "_maxresults", "_query", "_type");

    private static readonly FrozenSet<string> SingletonControls =
        FrozenSet.Create(StringComparer.Ordinal, "_count", "_summary", "_total", "_sort", "_format");

    // `+` decodes to a space, so an unencoded application/fhir+json arrives as "application/fhir json".
    private static readonly FrozenSet<string> JsonFormats =
        FrozenSet.Create(StringComparer.Ordinal, "json", "application/json", "application/fhir+json", "application/fhir json");

    private readonly SearchCapabilities _capabilities;
    private readonly FrozenSet<string> _passthrough;
    private readonly int _defaultPageSize;
    private readonly int _maxPageSize;

    /// <summary>Initializes a new instance of the <see cref="SearchPlanner"/> class.</summary>
    /// <param name="capabilities">What every shard can search.</param>
    /// <param name="chains">The validated chain options; listed paths resolve in Passthrough mode, all others Distributed.</param>
    /// <param name="limits">The validated limits; only the page sizes are read, and copied.</param>
    /// <exception cref="ArgumentOutOfRangeException">A page size is below 1.</exception>
    public SearchPlanner(SearchCapabilities capabilities, ChainResolutionOptions chains, LimitOptions limits)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.DefaultPageSize, 1, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxPageSize, 1, nameof(limits));
        _capabilities = capabilities;
        _passthrough = chains.Passthrough.ToFrozenSet(StringComparer.Ordinal);
        _maxPageSize = limits.MaxPageSize;
        _defaultPageSize = Math.Min(limits.DefaultPageSize, limits.MaxPageSize);
    }

    /// <summary>Plans a search of <paramref name="type"/>.</summary>
    /// <param name="type">The searched resource type.</param>
    /// <param name="query">The parsed query.</param>
    /// <returns>
    /// A continuation when the query is a sole <c>ct</c> token, which is not decoded here; otherwise a plan, or the first
    /// failure in occurrence order. Failures that involve several occurrences are reported after every single-occurrence
    /// check passed, at the later occurrence.
    /// </returns>
    public SearchPlanResult Plan(ResourceType type, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(query);
        if (!_capabilities.Searches(type))
        {
            return SearchPlanResult.Fail(SearchFailureCode.UnsupportedResourceType, SearchFailure.NoOrdinal);
        }

        if (query.Occurrences.FirstOrDefault(o => o.Key == ContinuationKey) is { } continuation)
        {
            return query.Occurrences.Count > 1 ? SearchPlanResult.Fail(SearchFailureCode.ContinuationNotAlone, continuation.Ordinal)
                : continuation.Value.Length == 0 ? SearchPlanResult.Fail(SearchFailureCode.InvalidValue, continuation.Ordinal)
                : SearchPlanResult.Continue(new ContinuationPlan(continuation));
        }

        var draft = new Draft(type);
        foreach (QueryOccurrence occurrence in query.Occurrences)
        {
            if (PlanOccurrence(draft, occurrence) is { } failure)
            {
                return SearchPlanResult.Fail(failure, occurrence.Ordinal);
            }
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (Outbound outbound in draft.Outbound)
        {
            if (!groups.Add(outbound.Group) && !_capabilities.EveryShard(c => c.AndRepetition.Contains(outbound.Type)))
            {
                return SearchPlanResult.Fail(SearchFailureCode.UnsupportedRepetition, outbound.Ordinal);
            }
        }

        string countShape = $"{type.Value}?{string.Join('&', draft.Outbound.Select(o => o.Shape).Order(StringComparer.Ordinal))}";
        if (draft.FirstCountRequest is int countRequest && !_capabilities.EveryShard(c => c.ExactCountShapes.Contains(countShape)))
        {
            return SearchPlanResult.Fail(SearchFailureCode.ExactCountNotCertified, countRequest);
        }

        if (FindUnprovenReferenceRewrite(draft) is { } unproven)
        {
            return SearchPlanResult.Fail(SearchFailureCode.UnsupportedChain, unproven.Occurrence.Ordinal);
        }

        return SearchPlanResult.Success(new QueryPlan(
            type,
            query,
            draft.Occurrences,
            draft.CountOnly ? null : draft.PageSize ?? _defaultPageSize,
            draft.Sort,
            draft.IncludeTotal,
            countShape,
            _capabilities.Registry));
    }

    private static string CodeOf(string key) => key.Split(':', 2)[0];

    private static string? ModifierOf(string key) => key.Split(':', 2) is [_, string modifier] ? modifier : null;

    // At projection time the definition is already in the all-shard intersection. One bare value can witness
    // an ordinary predicate; it cannot attest every value, a modifier, a repetition or a generated search.
    internal static bool CanPublishParameter(ResourceType type, SearchParameterInfo definition)
    {
        string code = definition.Code;
        if (code == ContinuationKey || Controls.Contains(code))
        {
            return false;
        }

        string? value = definition.Type switch
        {
            SearchParamType.String or SearchParamType.Token => "sample",
            SearchParamType.Date => "2000-01-01",
            SearchParamType.Number or SearchParamType.Quantity => "1",
            SearchParamType.Uri => "https://example.org/sample",
            _ => null, // No bare identity reference; composite and special syntax is definition-specific.
        };
        return value is not null
            && CompileSearch(type, code, value, out CompiledSearch search) is null
            && search is { Syntax: R4Search.ParameterKey, Ir: SearchParameterExpressionBase predicate }
            && BindsDefinition(predicate, definition);
    }

    private static bool BindsDefinition(SearchParameterExpressionBase compiled, SearchParameterInfo definition) =>
        compiled.Parameter.Code == definition.Code && compiled.Parameter.Type == definition.Type && compiled.Parameter.Url == definition.Url;

    // A count-shape descriptor: the decoded key, marked when its value has OR alternatives.
    private static string Shape(string key, bool alternatives) => alternatives ? key + "=," : key;

    private static PlannedOccurrence? FindUnprovenReferenceRewrite(Draft draft)
    {
        PlannedOccurrence[] forward = [.. draft.Occurrences.Where(o => o is { Kind: OccurrenceKind.ForwardChain, Action: OccurrenceAction.Replace })];
        if (forward.Length == 0)
        {
            return null;
        }

        // Unlike the count key, this evidence retains full values, order, sort and each generated list's provenance.
        string shape = $"{draft.Type.Value}?{string.Join('&', draft.Occurrences.Where(o => o.Action != OccurrenceAction.Remove).Select(ReferenceShapeTerm))}";
        string count = $"count:{shape}";
        return forward.FirstOrDefault(o => o.Hop!.Certifications.Values.Any(c =>
            (!draft.CountOnly && !c.ForwardSearchShapes.Contains(shape))
            || ((draft.CountOnly || draft.IncludeTotal) && !c.ForwardSearchShapes.Contains(count))));
    }

    private static string ReferenceShapeTerm(PlannedOccurrence occurrence)
    {
        if (occurrence.Action == OccurrenceAction.Replace)
        {
            return occurrence.Kind == OccurrenceKind.ReverseChain ? $"@has:{occurrence.Hop!.Path}" : $"@{occurrence.Hop!.Path}";
        }

        ParameterOccurrence kept = QueryRewriter.Encode(occurrence.Occurrence.Key, occurrence.Occurrence.Value);
        return $"{kept.Key}={kept.Value}";
    }

    /// <summary>
    /// Splits on unescaped commas; a backslash escapes the next character (FHIR R4 search §3.1.1.1.7). Returns each
    /// alternative's span in the decoded value, or <see langword="null"/> when one is empty or a backslash ends the value.
    /// </summary>
    private static List<(int Start, int Length)>? ReadAlternatives(string value)
    {
        var alternatives = new List<(int Start, int Length)>();
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == ',')
            {
                if (i == start)
                {
                    return null;
                }

                alternatives.Add((start, i - start));
                start = i + 1;
            }
            else if (value[i] == '\\' && ++i == value.Length)
            {
                return null;
            }
        }

        if (start == value.Length)
        {
            return null;
        }

        alternatives.Add((start, value.Length - start));
        return alternatives;
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private SearchFailureCode? PlanOccurrence(Draft draft, QueryOccurrence occurrence)
    {
        if (occurrence.Value.Length == 0)
        {
            return SearchFailureCode.InvalidValue;
        }

        string name = CodeOf(occurrence.Key);
        return Controls.Contains(name) ? PlanControl(draft, occurrence, name) : PlanSearch(draft, occurrence);
    }

    private SearchFailureCode? PlanControl(Draft draft, QueryOccurrence occurrence, string name)
    {
        if (name.Length != occurrence.Key.Length)
        {
            return name is Include or RevInclude ? SearchFailureCode.UnsupportedInclude : SearchFailureCode.UnsupportedParameter;
        }

        if (SingletonControls.Contains(name) && !draft.Singletons.Add(name))
        {
            return SearchFailureCode.DuplicateControl;
        }

        string value = occurrence.Value;
        switch (name)
        {
            case "_count":
                if (!value.All(char.IsAsciiDigit))
                {
                    return SearchFailureCode.InvalidValue;
                }

                string digits = value.TrimStart('0');
                if (digits.Length == 0)
                {
                    draft.RequestCount(occurrence.Ordinal, countOnly: true);
                }
                else
                {
                    // More than nine digits exceeds any page size, so it is bounded without parsing.
                    draft.PageSize = digits.Length > 9 ? _maxPageSize : Math.Min(int.Parse(digits, CultureInfo.InvariantCulture), _maxPageSize);
                }

                break;
            case "_summary" when value == "count":
                draft.RequestCount(occurrence.Ordinal, countOnly: true);
                break;
            case "_summary":
                return value is "true" or "text" or "data" or "false" ? SearchFailureCode.UnsupportedParameter : SearchFailureCode.InvalidValue;
            case "_total" when value is "accurate" or "estimate":
                // An estimate is answered exactly or not at all: there is no federated estimate to fall back to.
                draft.RequestCount(occurrence.Ordinal, countOnly: false);
                break;
            case "_total":
                if (value != "none")
                {
                    return SearchFailureCode.InvalidValue;
                }

                break;
            case "_format":
                if (!JsonFormats.Contains(value))
                {
                    return SearchFailureCode.UnsupportedFormat;
                }

                break;
            case "_sort":
                if (value is not ("_lastUpdated" or "-_lastUpdated") || !_capabilities.SupportsLastUpdatedSort)
                {
                    return SearchFailureCode.UnsupportedSort;
                }

                draft.Sort = value[0] == '-' ? LastUpdatedSort.Descending : LastUpdatedSort.Ascending;
                draft.Occurrences.Add(new PlannedOccurrence(occurrence, OccurrenceKind.Sort, OccurrenceAction.Keep, null));
                return null;
            case Include:
                return PlanInclude(draft, occurrence, reverse: false);
            case RevInclude:
                return PlanInclude(draft, occurrence, reverse: true);
            default:
                return SearchFailureCode.UnsupportedParameter;
        }

        draft.Occurrences.Add(new PlannedOccurrence(occurrence, OccurrenceKind.Control, OccurrenceAction.Remove, null));
        return null;
    }

    /// <summary>
    /// Plans <c>_include=Source:code[:Target]</c> or <c>_revinclude</c>, which the federation answers itself: by extracting
    /// references from the page, or by a follow-up search of the source type.
    /// </summary>
    private SearchFailureCode? PlanInclude(Draft draft, QueryOccurrence occurrence, bool reverse)
    {
        if (!_capabilities.TryAdmitInclude(draft.Type, occurrence.Value, reverse, out ReferenceHop? hop, out SearchFailureCode failure))
        {
            return failure;
        }

        draft.Occurrences.Add(new PlannedOccurrence(
            occurrence,
            reverse ? OccurrenceKind.RevInclude : OccurrenceKind.Include,
            OccurrenceAction.Remove,
            hop));
        return null;
    }

    private SearchFailureCode? PlanSearch(Draft draft, QueryOccurrence occurrence)
    {
        if (CompileSearch(draft.Type, occurrence.Key, occurrence.Value, out CompiledSearch search) is { } failure)
        {
            return failure;
        }

        return (search.Syntax, search.Ir) switch
        {
            (R4Search.ForwardChain, ChainedExpression { Reversed: false } chain) =>
                PlanForwardChain(draft, occurrence, chain, search.TerminalStart, search.HasAlternatives),
            (R4Search.ReverseChain, ChainedExpression { Reversed: true } chain) =>
                PlanReverseChain(draft, occurrence, chain, search.TerminalStart, search.HasAlternatives),
            (R4Search.ParameterKey, SearchParameterExpressionBase predicate) =>
                PlanPredicate(draft, occurrence, predicate, search.HasAlternatives),
            _ => search.Syntax == R4Search.ParameterKey ? SearchFailureCode.UnsupportedParameter : SearchFailureCode.UnsupportedChain,
        };
    }

    private static SearchFailureCode? CompileSearch(ResourceType type, string key, string value, out CompiledSearch search)
    {
        search = default;
        if (ReadAlternatives(value) is not { } alternatives)
        {
            return SearchFailureCode.InvalidValue;
        }

        // One hop only (design spec §7.5): refuse deeper chains before the compiler sees them.
        if (key.Count(c => c == '.') + CountOf(key, "_has:") > 1)
        {
            return SearchFailureCode.UnsupportedChain;
        }

        // Ignored outcomes carry no syntax, so only the key text can say a chain was meant.
        SearchFailureCode notCompiled = key.Contains('.', StringComparison.Ordinal) || key.StartsWith("_has", StringComparison.Ordinal)
            ? SearchFailureCode.UnsupportedChain
            : SearchFailureCode.UnsupportedParameter;
        if (R4Search.Compile(type, key, value, notCompiled, out SearchFailureCode failure) is not { } trace)
        {
            return failure;
        }

        // Only what the compiler read exactly as the federation reads and forwards it is planned.
        if (!R4Search.TryReadKey(trace, out string syntax, out int terminalStart))
        {
            return notCompiled;
        }

        if (!R4Search.ReadsAlternatives(trace, alternatives))
        {
            return SearchFailureCode.UnsupportedAlternatives;
        }

        // A caller's reference value names a resource by identity, which depends on the shard holding the reference, so no
        // shard matches it exactly for the federation. `:missing` is the only reference filter the compiler binds that is not one.
        SearchParameterExpressionBase? filter = trace.Ir switch
        {
            ChainedExpression { Expression: SearchParameterExpressionBase terminal } => terminal,
            SearchParameterExpressionBase predicate => predicate,
            _ => null,
        };
        if (filter is { Parameter.Type: SearchParamType.Reference } and not MissingSearchParameterExpression)
        {
            return alternatives.All(a => R4Search.IsReferenceValue(value.Substring(a.Start, a.Length)))
                ? SearchFailureCode.UnsupportedReferenceFilter
                : SearchFailureCode.InvalidValue;
        }

        if ((syntax == R4Search.ParameterKey) != (notCompiled == SearchFailureCode.UnsupportedParameter))
        {
            return notCompiled;
        }

        search = new CompiledSearch(syntax, trace.Ir, terminalStart, alternatives.Count > 1);
        return null;
    }

    private SearchFailureCode? PlanPredicate(Draft draft, QueryOccurrence occurrence, SearchParameterExpressionBase compiled, bool alternatives)
    {
        string code = CodeOf(occurrence.Key);
        if (_capabilities.FindParameter(draft.Type, code) is not { } definition || !BindsDefinition(compiled, definition))
        {
            return SearchFailureCode.UnsupportedParameter;
        }

        // The compiled trace attests the compiler understood the modifier; the shards must still each prove it.
        if (ModifierOf(occurrence.Key) is { } modifier && !ModifierProven(definition.Type, modifier))
        {
            return SearchFailureCode.UnsupportedModifier;
        }

        if (alternatives && !OrProven(definition.Type))
        {
            return SearchFailureCode.UnsupportedAlternatives;
        }

        draft.Occurrences.Add(new PlannedOccurrence(occurrence, OccurrenceKind.Predicate, OccurrenceAction.Keep, null));
        draft.Outbound.Add(new Outbound(occurrence.Ordinal, code, definition.Type, Shape(occurrence.Key, alternatives)));
        return null;
    }

    /// <summary>
    /// Plans <c>code[:Target].terminal</c>, checking the compiler read the hop exactly as written; the compiler's terminal
    /// key starts at <c>terminalStart</c>, just after the hop's <c>.</c>.
    /// </summary>
    private SearchFailureCode? PlanForwardChain(Draft draft, QueryOccurrence occurrence, ChainedExpression chain, int terminalStart, bool alternatives)
    {
        string key = occurrence.Key;
        if (key[terminalStart - 1] != '.')
        {
            return SearchFailureCode.UnsupportedChain;
        }

        string hop = key[..(terminalStart - 1)];
        string terminalKey = key[terminalStart..];
        string code = CodeOf(hop);
        string? typedName = ModifierOf(hop);
        ResourceType? typed = null;
        if (chain.ReferenceSearchParameter.Code != code
            || !chain.ResourceTypes.SequenceEqual([draft.Type.Value])
            || chain.Expression is not SearchParameterExpressionBase terminal
            || terminal.Parameter.Code != CodeOf(terminalKey)
            || (typedName is not null && (!ResourceType.TryParse(typedName, out typed) || !chain.TargetResourceTypes.SequenceEqual([typed.Value])))
            || R4Search.FindDefinition(draft.Type, code) is not { } definition)
        {
            return SearchFailureCode.UnsupportedChain;
        }

        ResourceType target;
        if (typed is not null)
        {
            target = typed;
        }
        else if (definition.TargetResourceTypes.ToArray() is [string only])
        {
            target = ResourceType.Parse(only);
        }
        else
        {
            // The compiler picks one target for an untyped polymorphic hop; the federation does not guess (design spec §7.5).
            return SearchFailureCode.ChainTargetAmbiguous;
        }

        return PlanHop(draft, occurrence, OccurrenceKind.ForwardChain, new ReferencePath(draft.Type, code, target), definition, terminalKey, alternatives);
    }

    /// <summary>
    /// Plans <c>_has:Source:code:terminal</c>, checking the compiler read the hop exactly as written; the compiler's
    /// terminal key starts at <c>terminalStart</c>, just after the hop's last <c>:</c>.
    /// </summary>
    private SearchFailureCode? PlanReverseChain(Draft draft, QueryOccurrence occurrence, ChainedExpression chain, int terminalStart, bool alternatives)
    {
        string key = occurrence.Key;
        string terminalKey = key[terminalStart..];
        if (key[terminalStart - 1] != ':'
            || key[..(terminalStart - 1)].Split(':') is not ["_has", string sourceName, string code]
            || !ResourceType.TryParse(sourceName, out ResourceType? source)
            || chain.ReferenceSearchParameter.Code != code
            || !chain.ResourceTypes.SequenceEqual([source.Value])
            || chain.Expression is not SearchParameterExpressionBase terminal
            || terminal.Parameter.Code != CodeOf(terminalKey)
            || R4Search.FindDefinition(source, code) is not { } definition
            || !definition.TargetResourceTypes.Contains(draft.Type.Value, StringComparer.Ordinal))
        {
            return SearchFailureCode.UnsupportedChain;
        }

        return PlanHop(draft, occurrence, OccurrenceKind.ReverseChain, new ReferencePath(source, code, draft.Type), definition, terminalKey, alternatives);
    }

    /// <summary>The checks a forward and a reverse hop share, once the hop's reference path is known.</summary>
    private SearchFailureCode? PlanHop(
        Draft draft,
        QueryOccurrence occurrence,
        OccurrenceKind kind,
        ReferencePath path,
        SearchParameterInfo definition,
        string terminalKey,
        bool alternatives)
    {
        // The terminal is evaluated on the path's far side: the target of a forward hop, the source of a reverse one.
        bool forward = kind == OccurrenceKind.ForwardChain;
        ResourceType terminalType = forward ? path.Target : path.Source;
        string? terminalModifier = ModifierOf(terminalKey);
        if (terminalModifier is "missing" or "not"
            || !R4Search.IsPlainReference(definition, path.Source)
            || !_capabilities.Searches(terminalType))
        {
            return SearchFailureCode.UnsupportedChain;
        }

        if (_capabilities.FindParameter(path.Source, path.Code) is null
            || _capabilities.FindParameter(terminalType, CodeOf(terminalKey)) is not { } terminal)
        {
            return SearchFailureCode.UnsupportedParameter;
        }

        // A distributed reverse hop is answered by `_id=` alternatives on the searched type, which must be advertised.
        bool passthrough = _passthrough.Contains(path.ToString());
        if (!forward && !passthrough && _capabilities.FindParameter(path.Target, "_id") is null)
        {
            return SearchFailureCode.UnsupportedParameter;
        }

        ReferencePathOperations required = (forward, passthrough) switch
        {
            (true, true) => ReferencePathOperations.NativeForward,
            (false, true) => ReferencePathOperations.NativeReverse,
            (true, false) => ReferencePathOperations.DistributedForward,
            (false, false) => ReferencePathOperations.DistributedReverse,
        };
        if (_capabilities.CertifiedPath(path, required) is not { } certifications)
        {
            return SearchFailureCode.UnsupportedChain;
        }

        if (terminalModifier is not null && !ModifierProven(terminal.Type, terminalModifier))
        {
            return SearchFailureCode.UnsupportedModifier;
        }

        // A distributed hop sends the resolved keys as OR alternatives: references forward, ids in reverse.
        SearchParamType outboundType = passthrough || forward ? SearchParamType.Reference : SearchParamType.Token;
        if ((alternatives && !OrProven(terminal.Type)) || (!passthrough && !OrProven(outboundType)))
        {
            return SearchFailureCode.UnsupportedAlternatives;
        }

        ReferenceHop hop = new(
            path,
            definition.Url,
            definition.Expression,
            passthrough ? ChainResolutionMode.Passthrough : ChainResolutionMode.Distributed,
            passthrough ? null : QueryRewriter.Encode(terminalKey, occurrence.Value),
            certifications);
        draft.Occurrences.Add(new PlannedOccurrence(occurrence, kind, passthrough ? OccurrenceAction.Keep : OccurrenceAction.Replace, hop));
        string group = passthrough ? occurrence.Key : forward ? path.Code : "_id";
        draft.Outbound.Add(new Outbound(occurrence.Ordinal, group, outboundType, passthrough ? Shape(occurrence.Key, alternatives) : group + "=,"));
        return null;
    }

    // A resource-type modifier on a reference is certified once, as `[type]`, not per type name.
    private bool ModifierProven(SearchParamType type, string modifier)
    {
        string certified = type == SearchParamType.Reference && R4Search.IsResourceType(modifier) ? SearchCertification.TypeModifier : modifier;
        return _capabilities.EveryShard(c => c.SupportsModifier(type, certified));
    }

    private bool OrProven(SearchParamType type) => _capabilities.ProvesAlternatives(type);

    private readonly record struct CompiledSearch(string Syntax, Expression? Ir, int TerminalStart, bool HasAlternatives);

    /// <summary>One outbound backend parameter: its AND group, value type, and count-shape descriptor.</summary>
    private sealed record Outbound(int Ordinal, string Group, SearchParamType Type, string Shape);

    /// <summary>The mutable state of one <see cref="Plan"/> call; never shared.</summary>
    private sealed class Draft(ResourceType type)
    {
        public ResourceType Type { get; } = type;

        public List<PlannedOccurrence> Occurrences { get; } = [];

        public List<Outbound> Outbound { get; } = [];

        public HashSet<string> Singletons { get; } = new(StringComparer.Ordinal);

        public int? PageSize { get; set; }

        public LastUpdatedSort? Sort { get; set; }

        public bool CountOnly { get; private set; }

        public bool IncludeTotal { get; private set; }

        /// <summary>Gets the ordinal of the first occurrence that asked for an exact count.</summary>
        public int? FirstCountRequest { get; private set; }

        public void RequestCount(int ordinal, bool countOnly)
        {
            FirstCountRequest ??= ordinal;
            CountOnly |= countOnly;
            IncludeTotal |= !countOnly;
        }
    }
}
