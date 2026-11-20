using System.Collections.ObjectModel;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// What a shard advertises for one resource type: its search parameters and its <c>searchInclude</c> and
/// <c>searchRevInclude</c> strings (design spec §9.1). <see cref="SearchCapabilities.Resources"/> publishes the
/// federation-wide intersection in the same shape.
/// </summary>
public sealed class ResourceSearchSupport
{
    /// <summary>Initializes a new instance of the <see cref="ResourceSearchSupport"/> class.</summary>
    /// <param name="type">The resource type.</param>
    /// <param name="parameters">The advertised search parameters; may repeat a code, which admission treats as a conflict unless identical.</param>
    /// <param name="searchIncludes">The advertised <c>searchInclude</c> values, such as <c>Patient:organization</c>, compared exactly.</param>
    /// <param name="searchRevIncludes">The advertised <c>searchRevInclude</c> values, such as <c>Observation:subject</c>, compared exactly.</param>
    public ResourceSearchSupport(
        ResourceType type,
        IEnumerable<SearchParameterSupport> parameters,
        IEnumerable<string> searchIncludes,
        IEnumerable<string> searchRevIncludes)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Parameters = new ReadOnlyCollection<SearchParameterSupport>([.. parameters]);
        SearchIncludes = new ReadOnlyCollection<string>([.. searchIncludes]);
        SearchRevIncludes = new ReadOnlyCollection<string>([.. searchRevIncludes]);
    }

    /// <summary>Gets the resource type.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the search parameters.</summary>
    public IReadOnlyList<SearchParameterSupport> Parameters { get; }

    /// <summary>Gets the <c>searchInclude</c> values.</summary>
    public IReadOnlyList<string> SearchIncludes { get; }

    /// <summary>Gets the <c>searchRevInclude</c> values.</summary>
    public IReadOnlyList<string> SearchRevIncludes { get; }
}
