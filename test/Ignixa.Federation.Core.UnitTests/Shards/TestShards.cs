using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

/// <summary>Builds valid shard configuration for registry tests, so each test changes only what it is about.</summary>
internal static class TestShards
{
    public static readonly BackendProfile MicrosoftFhirServer = new(
        "MicrosoftFhirServer",
        SupportsPostSearch: true,
        MaxCount: 1000,
        NextLinkRedemption.RepostToSearch,
        new LastUpdatedOrder(TimeSpan.FromMilliseconds(1), StableTies: true));

    public static readonly BackendProfile IgnixaProfile = new("Ignixa", SupportsPostSearch: true, MaxCount: 1000, NextLinkRedemption.FollowGet, null);

    public static IReadOnlyList<BackendProfile> Profiles { get; } = [MicrosoftFhirServer, IgnixaProfile];

    public static ShardOptions Shard(string id, string alias, string baseUrl, params string[] linkBaseAliases)
    {
        var shard = new ShardOptions
        {
            Id = id,
            Alias = alias,
            BaseUrl = baseUrl,
            Profile = MicrosoftFhirServer.Name,
            Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.example" },
        };

        foreach (string linkBaseAlias in linkBaseAliases)
        {
            shard.LinkBaseAliases.Add(linkBaseAlias);
        }

        return shard;
    }

    public static ShardOptions East(params string[] linkBaseAliases) => Shard("east", "k7q2", "https://east.example/fhir", linkBaseAliases);

    public static ShardOptions West(params string[] linkBaseAliases) => Shard("west", "p9xm", "https://west.example/fhir", linkBaseAliases);

    public static ShardOptions WithProfile(this ShardOptions shard, string profile)
    {
        shard.Profile = profile;
        return shard;
    }

    public static ShardRegistry Create(params ShardOptions[] shards) => ShardRegistry.Create(shards, Profiles);

    public static IReadOnlyList<string> Errors(params ShardOptions[] shards)
    {
        ShardRegistry.TryCreate(shards, Profiles, out ShardRegistry? registry, out IReadOnlyList<string> errors).ShouldBeFalse();
        registry.ShouldBeNull();
        return errors;
    }

    /// <summary>Asserts that the configuration fails with exactly one error, at <paramref name="path"/>.</summary>
    public static string SingleError(string path, params ShardOptions[] shards)
    {
        string error = Errors(shards).ShouldHaveSingleItem();
        error.ShouldStartWith(path + ": ");
        return error;
    }
}
