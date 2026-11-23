using Ignixa.Federation.Api.Continuations;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// The trusted current revisions of shards and parameter definitions, standing in for the registry and the
/// definition catalogue. Records every lookup so a test can see exactly which dependencies redemption consulted.
/// </summary>
internal sealed class RevisionCatalog
{
    private readonly Dictionary<(ContinuationDependencyGroup Group, string Key), string> _current = new()
    {
        [(ContinuationDependencyGroup.Shard, ContinuationTestKit.ShardA)] = "rev-a1",
        [(ContinuationDependencyGroup.Shard, ContinuationTestKit.ShardB)] = "rev-b1",
        [(ContinuationDependencyGroup.Definition, ContinuationTestKit.CodeDefinition)] = "def-1",
    };

    private readonly List<(ContinuationDependencyGroup Group, string Key)> _consulted = [];

    public IReadOnlyList<(ContinuationDependencyGroup Group, string Key)> Consulted => _consulted;

    public void Set(ContinuationDependencyGroup group, string key, string revision) => _current[(group, key)] = revision;

    public void Remove(ContinuationDependencyGroup group, string key) => _current.Remove((group, key));

    public string? Lookup(ContinuationDependencyGroup group, string key)
    {
        _consulted.Add((group, key));
        return _current.GetValueOrDefault((group, key));
    }
}
