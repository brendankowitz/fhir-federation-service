using Ignixa.Abstractions;
using Ignixa.Federation.Core.Identity;
using Ignixa.Search.Models;
using Ignixa.Search.Parsing;

namespace Ignixa.Federation.Core.Search;

/// <summary>Release-specific search semantics for one federation view.</summary>
public interface IFederationSearchSpecification
{
    /// <summary>Gets the selected Ignixa release.</summary>
    FhirVersion Version { get; }

    /// <summary>Gets the published version of the selected schema.</summary>
    string PublishedVersion { get; }

    /// <summary>Gets the schema used by this view's compiler and reference extraction.</summary>
    IFhirSchemaProvider Schema { get; }

    /// <summary>Gets the exact shard versions accepted by this view.</summary>
    IReadOnlySet<string> AcceptedShardVersions { get; }

    /// <summary>Determines whether the exact name is a resource type in this release.</summary>
    bool IsResourceType(string name);

    /// <summary>Finds the exact search parameter definition on a resource type.</summary>
    SearchParameterInfo? FindDefinition(ResourceType type, string code);

    /// <summary>Compiles one decoded occurrence, or returns a fail-closed reason.</summary>
    ParameterTrace? Compile(ResourceType type, string key, string value, SearchFailureCode notCompiled, out SearchFailureCode failure);

    /// <summary>Determines whether the definition selects only reference elements.</summary>
    bool IsPlainReference(SearchParameterInfo definition, ResourceType source);

    /// <summary>Determines whether a reference value is well formed for this release.</summary>
    bool IsReferenceValue(string alternative);
}
