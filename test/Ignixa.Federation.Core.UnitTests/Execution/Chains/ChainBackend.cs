using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using System.Net;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// Synthetic stored resources served through a <see cref="FakeShardClient"/> like a strict backend: a reference
/// parameter matches a stored reference string literally, OR lists split on unescaped commas, and a native chain resolves
/// only relative references to resources on the same shard. Any other parameter is rejected, as strict handling would.
/// </summary>
/// <remarks>Not native certification: it only gives the resolver's requests observable consequences.</remarks>
internal sealed class ChainBackend
{
    private readonly List<Stored> _stored = [];

    public ChainBackend(ShardRegistry registry)
    {
        foreach (ShardDescriptor shard in registry.Shards)
        {
            Client.OnSearch(shard.Id, (request, _) => Task.FromResult(Search(request)));
        }
    }

    public FakeShardClient Client { get; } = new();

    public ChainBackend Patient(ShardId shard, string id, string name) =>
        Add(shard, ChainScenario.Patient, id, ("name", name));

    public ChainBackend Practitioner(ShardId shard, string id, string name) =>
        Add(shard, ChainScenario.Practitioner, id, ("name", name));

    public ChainBackend Observation(ShardId shard, string id, string subject, string code = "a", string? performer = null) =>
        performer is null
            ? Add(shard, ChainScenario.Observation, id, ("subject", subject), ("code", code))
            : Add(shard, ChainScenario.Observation, id, ("subject", subject), ("code", code), ("performer", performer));

    /// <summary>Splits a decoded FHIR search value on unescaped commas and removes the escapes.</summary>
    public static IReadOnlyList<string> Alternatives(string value)
    {
        var alternatives = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[++i]);
            }
            else if (value[i] == ',')
            {
                alternatives.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }

        alternatives.Add(current.ToString());
        return alternatives;
    }

    private ChainBackend Add(ShardId shard, ResourceType type, string id, params (string Name, string Value)[] fields)
    {
        _stored.Add(new Stored(new ResourceKey(shard, type, ResourceId.Parse(id)), fields.ToDictionary(f => f.Name, f => f.Value)));
        return this;
    }

    private ShardPage Search(ShardSearchRequest request)
    {
        IEnumerable<Stored> matches = _stored.Where(s => s.Key.Shard == request.Shard && s.Key.Type == request.Type);
        foreach (ParameterOccurrence parameter in request.Parameters)
        {
            // One form decode, as a backend reads a query: '+' is a space and '%2B' a literal plus.
            string key = WebUtility.UrlDecode(parameter.Key);
            string value = WebUtility.UrlDecode(parameter.Value);
            matches = key switch
            {
                "_elements" when value == "id" => matches,
                _ when key.Contains('.', StringComparison.Ordinal) => NativeChain(matches, request.Shard, key, value),
                _ => Field(matches, key.Split(':')[0], value),
            };
        }

        Stored[] found = [.. matches];
        if (found.Length > request.PageSize)
        {
            throw new InvalidOperationException("The synthetic backend does not page.");
        }

        return new ShardPage(found.Select(s => Fixture.Entry(s.Key)), null, null);
    }

    private static IEnumerable<Stored> Field(IEnumerable<Stored> candidates, string name, string value)
    {
        if (name is not ("name" or "subject" or "code" or "performer"))
        {
            throw new InvalidOperationException($"Strict handling rejects '{name}'.");
        }

        IReadOnlyList<string> alternatives = Alternatives(value);
        return [.. candidates.Where(s => s.Fields.TryGetValue(name, out string? stored) && alternatives.Contains(stored, StringComparer.Ordinal))];
    }

    // `subject:Patient.name=x`: the backend joins only relative references to its own resources.
    private IEnumerable<Stored> NativeChain(IEnumerable<Stored> candidates, ShardId shard, string key, string value)
    {
        string[] hop = key.Split('.', 2);
        string[] reference = hop[0].Split(':');
        HashSet<string> targets = [.. Field(
            _stored.Where(s => s.Key.Shard == shard && s.Key.Type.Value == reference[1]),
            hop[1].Split(':')[0],
            value).Select(s => $"{s.Key.Type.Value}/{s.Key.Id.Value}")];
        return [.. candidates.Where(s => s.Fields.TryGetValue(reference[0], out string? stored) && targets.Contains(stored))];
    }

    private sealed record Stored(ResourceKey Key, IReadOnlyDictionary<string, string> Fields);
}
