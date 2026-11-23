using System.Collections.Frozen;
using Ignixa.Abstractions;
using Ignixa.Federation.Core.Identity;
using Ignixa.Search.Definition;
using Ignixa.Search.Exceptions;
using Ignixa.Search.Indexing;
using Ignixa.Search.Models;
using Ignixa.Search.Parsing;
using Ignixa.Serialization.Abstractions;

namespace Ignixa.Federation.Core.Search;

/// <summary>Schema-backed search semantics for a single release.</summary>
public sealed class FederationSearchSpecification : IFederationSearchSpecification
{
    private readonly IFhirSchemaProvider _schema;
    private readonly SearchParameterDefinitionManager _definitions;
    private readonly Func<SearchOptionsBuilder> _createCompiler;
    private readonly FrozenSet<string> _resourceNames;

    /// <summary>Creates a specification using the composition's schema, base manager and per-call compiler factory.</summary>
    /// <param name="version">The schema's release.</param>
    /// <param name="schema">The schema shared by this view.</param>
    /// <param name="definitions">The base definition manager for this schema.</param>
    /// <param name="createCompiler">Creates a fresh compiler for each occurrence.</param>
    /// <param name="acceptedShardVersions">The exact release versions accepted from shards.</param>
    public FederationSearchSpecification(
        FhirVersion version,
        IFhirSchemaProvider schema,
        SearchParameterDefinitionManager definitions,
        Func<SearchOptionsBuilder> createCompiler,
        IEnumerable<string> acceptedShardVersions)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(createCompiler);
        ArgumentNullException.ThrowIfNull(acceptedShardVersions);
        if (schema.Version != version)
        {
            throw new ArgumentException("The selected release must match the schema.", nameof(version));
        }

        Version = version;
        PublishedVersion = schema.FullVersion;
        AcceptedShardVersions = acceptedShardVersions.ToFrozenSet(StringComparer.Ordinal);
        _schema = schema;
        _definitions = definitions;
        _createCompiler = createCompiler;
        _resourceNames = schema.ResourceTypeNames.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public FhirVersion Version { get; }

    /// <inheritdoc/>
    public string PublishedVersion { get; }

    /// <inheritdoc/>
    public IFhirSchemaProvider Schema => _schema;

    /// <inheritdoc/>
    public IReadOnlySet<string> AcceptedShardVersions { get; }

    /// <inheritdoc/>
    public bool IsResourceType(string name) => _resourceNames.Contains(name);

    /// <inheritdoc/>
    public SearchParameterInfo? FindDefinition(ResourceType type, string code)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _definitions.TryGetSearchParameter(type.Value, code, out SearchParameterInfo? definition) && definition.Code == code
            ? definition
            : null;
    }

    /// <inheritdoc/>
    public ParameterTrace? Compile(ResourceType type, string key, string value, SearchFailureCode notCompiled, out SearchFailureCode failure)
    {
        ArgumentNullException.ThrowIfNull(type);
        var traces = new List<ParameterTrace>(1);
        failure = notCompiled;
        try
        {
            _createCompiler().Build(type.Value, [new QueryParameter(key, value)], _schema, traces);
        }
        catch (FhirException e) when (e is SearchParameterNotSupportedException or SearchOperationNotSupportedException
            or SearchModifierNotSupportedException or SearchResourceNotSupportedException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
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

        return SearchSyntax.CompiledTrace(traces, key, value, ref failure);
    }

    /// <inheritdoc/>
    public bool IsPlainReference(SearchParameterInfo definition, ResourceType source)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(source);
        string prefix = source.Value + ".";
        string[] terms =
        [
            .. definition.Expression.Split('|')
                .Select(t => t.Trim())
                .Select(t => t.StartsWith('(', StringComparison.Ordinal) && t.EndsWith(')', StringComparison.Ordinal) ? t[1..^1].Trim() : t)
                .Where(t => t.StartsWith(prefix, StringComparison.Ordinal)),
        ];
        return terms.Length > 0 && terms.All(t => SearchSyntax.TryReadReferencePath(t, out string? path) && IsReferenceElement(path));
    }

    /// <inheritdoc/>
    public bool IsReferenceValue(string alternative) => SearchSyntax.IsReferenceValue(alternative, _resourceNames);

    private bool IsReferenceElement(string path)
    {
        string[] steps = path.Split('.');
        string owner = steps[0];
        for (int i = 1; i < steps.Length; i++)
        {
            ITypeExtended? element = _schema.GetTypeDefinition(owner)?.Children
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

            owner = code is "BackboneElement" or "Element" ? $"{owner}.{steps[i]}" : code;
        }

        return false;
    }
}
