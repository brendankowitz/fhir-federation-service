using System.Text;
using System.Text.Json;
using Ignixa.Federation.Api.Responses;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Responses;

/// <summary>
/// Executes the real <see cref="IResult"/> against a <see cref="DefaultHttpContext"/> and inspects the bytes a client
/// would receive (design spec §7.1, §7.2, §11). The shard collision comes from the committed federation-oracle manifest.
/// </summary>
public sealed class SearchsetResponseTests
{
    private const string ProxyBase = "https://federation.example.org/fhir";
    private const string Self = "https://federation.example.org/fhir/Patient?name=Orchid&_count=2&identifier=a%2fb%7Cc%2B";
    private const string Next = "https://federation.example.org/fhir/Patient?ct=Opaque.Token_1-x";

    // Deliberately awkward spelling: leading/trailing whitespace, JSON escapes and a relative reference.
    private static readonly byte[] EastBytes = Encoding.UTF8.GetBytes(
        " {\r\n  \"resourceType\" : \"Patient\",\"id\":\"dup\",\n\t"
        + "\"name\":[{\"family\":\"Orchid\",\"given\":[\"Eli\"],\"text\":\"caf\\u00e9 \\\"q\\\" \\/ \\ud83d\\ude00 é <&>\"}],"
        + "\"managingOrganization\":{\"reference\":\"Organization/org1\"}  }\r\n");

    private static readonly byte[] WestBytes = Encoding.UTF8.GetBytes(
        "{\"resourceType\":\"Patient\",\"id\":\"dup\",\"name\":[{\"family\":\"Orchid\",\"given\":[\"Gus\"]}]}");

    private static readonly byte[] ObservationBytes = Encoding.UTF8.GetBytes(
        "{\"resourceType\":\"Observation\", \"id\":\"obs-dup\",\"status\":\"final\",\"code\":{\"text\":\"Example\"},"
        + "\"subject\":{\"reference\":\"Patient/dup\"},\"valueQuantity\":{\"value\":2.00}}");

    private static readonly byte[] OutcomeBytes = Encoding.UTF8.GetBytes(
        "\t{ \"resourceType\" : \"OperationOutcome\",\r\n \"issue\":[{\"severity\":\"warning\",\"code\":\"incomplete\",\"diagnostics\":\"included resources truncated \\u2026\"}] }\n");

    [Fact]
    public async Task CollidingIdsOnTwoShards_KeepTwoFullUrlsAndTheirOwnBodiesAndAliases()
    {
        Collision collision = LoadCollision();

        JsonElement[] entries = await EntriesAsync(Create(collision.Registry, [collision.East, collision.West]));

        entries.Length.ShouldBe(2);
        foreach (JsonElement entry in entries)
        {
            string fullUrl = entry.GetProperty("fullUrl").GetString()!;
            string given = entry.GetProperty("resource").GetProperty("name")[0].GetProperty("given")[0].GetString()!;
            given.ShouldBe(collision.ExpectedGiven[fullUrl]);
        }

        entries[0].GetProperty("fullUrl").GetString().ShouldBe("https://fhir-east.example.org/Patient/dup");
        entries[1].GetProperty("fullUrl").GetString().ShouldBe("https://fhir-west.example.org/Patient/dup");
        SourceShard(entries[0]).ShouldBe("k7q2");
        SourceShard(entries[1]).ShouldBe("p9xm");
        AlternateUrl(entries[0]).ShouldBe($"{ProxyBase}/shards/k7q2/Patient/dup");
        AlternateUrl(entries[1]).ShouldBe($"{ProxyBase}/shards/p9xm/Patient/dup");
    }

    [Fact]
    public async Task ResourceBytes_AreWrittenUnchanged()
    {
        Collision collision = LoadCollision();

        ShardEntry include = Entry("west", "Observation", "obs-dup", ObservationBytes);

        byte[] body = await BodyAsync(Create(collision.Registry, [collision.East, collision.West], includes: [include]));

        CountOccurrences(body, EastBytes).ShouldBe(1);
        CountOccurrences(body, WestBytes).ShouldBe(1);
        CountOccurrences(body, ObservationBytes).ShouldBe(1);
        IndexOf(body, EastBytes).ShouldBeLessThan(IndexOf(body, WestBytes));
    }

    [Fact]
    public async Task MatchesIncludesAndOutcomes_AreGroupedInThatOrderWithTheirModes()
    {
        Collision collision = LoadCollision();
        ShardEntry observation = Entry("west", "Observation", "obs-dup", ObservationBytes);

        JsonElement[] entries = await EntriesAsync(
            Create(collision.Registry, [collision.East, collision.West], includes: [observation], outcomes: [OutcomeBytes]));

        entries.Select(e => e.GetProperty("search").GetProperty("mode").GetString())
            .ShouldBe(["match", "match", "include", "outcome"]);
        entries.Any(e => e.GetProperty("search").TryGetProperty("score", out _)).ShouldBeFalse();

        JsonElement include = entries[2];
        include.GetProperty("fullUrl").GetString().ShouldBe("https://fhir-west.example.org/Observation/obs-dup");
        SourceShard(include).ShouldBe("p9xm");
        AlternateUrl(include).ShouldBe($"{ProxyBase}/shards/p9xm/Observation/obs-dup");
        include.GetProperty("resource").GetProperty("subject").GetProperty("reference").GetString().ShouldBe("Patient/dup");

        JsonElement outcome = entries[3];
        outcome.GetProperty("resource").GetProperty("resourceType").GetString().ShouldBe("OperationOutcome");
    }

    [Fact]
    public async Task IdenticalOutcomes_KeepTheirBytesAndGetDistinctBundleLocalUrlsWithoutShardProvenance()
    {
        Collision collision = LoadCollision();

        byte[] body = await BodyAsync(Create(collision.Registry, [], outcomes: [OutcomeBytes, OutcomeBytes]));

        CountOccurrences(body, OutcomeBytes).ShouldBe(2);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement entries = document.RootElement.GetProperty("entry");
        entries.GetArrayLength().ShouldBe(2);

        const string prefix = "urn:uuid:";
        var ids = new HashSet<Guid>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            entry.TryGetProperty("fullUrl", out JsonElement url).ShouldBeTrue();
            string fullUrl = url.GetString()!;
            fullUrl.ShouldStartWith(prefix);
            Guid.TryParseExact(fullUrl.AsSpan(prefix.Length), "D", out Guid id).ShouldBeTrue();
            id.ShouldNotBe(Guid.Empty);
            ids.Add(id).ShouldBeTrue();
            entry.TryGetProperty("extension", out _).ShouldBeFalse();
            entry.TryGetProperty("link", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task EachSequenceKeepsItsSuppliedOrder()
    {
        Collision collision = LoadCollision();
        ShardEntry observation = Entry("west", "Observation", "obs-dup", ObservationBytes);
        ShardEntry organization = Entry("east", "Organization", "org1", """{"resourceType":"Organization","id":"org1"}"""u8);

        JsonElement[] entries = await EntriesAsync(
            Create(collision.Registry, [collision.West, collision.East], includes: [observation, organization]));

        entries.Select(e => e.GetProperty("fullUrl").GetString()).ShouldBe(
        [
            "https://fhir-west.example.org/Patient/dup",
            "https://fhir-east.example.org/Patient/dup",
            "https://fhir-west.example.org/Observation/obs-dup",
            "https://fhir-east.example.org/Organization/org1",
        ]);
    }

    [Fact]
    public async Task WithoutProxyReads_NoAlternateLinkButSourceShardExtensionRemains()
    {
        Collision collision = LoadCollision();

        JsonElement[] entries = await EntriesAsync(Create(collision.Registry, [collision.East], proxyReads: false));

        JsonElement entry = entries.ShouldHaveSingleItem();
        entry.TryGetProperty("link", out _).ShouldBeFalse();
        SourceShard(entry).ShouldBe("k7q2");
        entry.GetProperty("extension")[0].GetProperty("url").GetString()
            .ShouldBe("http://ignixa.io/federation/StructureDefinition/source-shard");
    }

    [Fact]
    public async Task AnEntryOfAnIneligibleType_KeepsItsBytesFullUrlAndSourceShardButHasNoAlternate_WhetherMatchOrInclude()
    {
        Collision collision = LoadCollision();
        ShardEntry observationMatch = Entry("east", "Observation", "obs-dup", ObservationBytes);
        ShardEntry observationInclude = Entry("west", "Observation", "obs-dup", ObservationBytes);
        ShardEntry organizationInclude = Entry("east", "Organization", "org1", "{\"resourceType\":\"Organization\",\"id\":\"org1\"}"u8);

        byte[] body = await BodyAsync(CreateEligible(
            collision.Registry,
            [collision.East, observationMatch],
            [observationInclude, organizationInclude],
            eligible: type => type.Value is "Patient" or "Organization"));

        JsonElement[] entries = [.. JsonDocument.Parse(body).RootElement.GetProperty("entry").EnumerateArray()];
        entries.Select(e => e.GetProperty("fullUrl").GetString()).ShouldBe(
        [
            "https://fhir-east.example.org/Patient/dup",
            "https://fhir-east.example.org/Observation/obs-dup",
            "https://fhir-west.example.org/Observation/obs-dup",
            "https://fhir-east.example.org/Organization/org1",
        ]);
        entries.Select(e => e.TryGetProperty("link", out _)).ShouldBe([true, false, false, true]);
        entries.Select(SourceShard).ShouldBe(["k7q2", "k7q2", "p9xm", "k7q2"]);
        AlternateUrl(entries[0]).ShouldBe($"{ProxyBase}/shards/k7q2/Patient/dup");
        AlternateUrl(entries[3]).ShouldBe($"{ProxyBase}/shards/k7q2/Organization/org1");
        entries.Select(e => e.GetProperty("search").GetProperty("mode").GetString()).ShouldBe(["match", "match", "include", "include"]);
        CountOccurrences(body, ObservationBytes).ShouldBe(2);
        IndexOf(body, EastBytes).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ProxyBaseWithTrailingSlash_DoesNotDoubleTheSlash()
    {
        Collision collision = LoadCollision();

        JsonElement[] entries = await EntriesAsync(
            Create(collision.Registry, [collision.East], proxyReadBase: new Uri(ProxyBase + "/")));

        AlternateUrl(entries.ShouldHaveSingleItem()).ShouldBe($"{ProxyBase}/shards/k7q2/Patient/dup");
    }

    [Fact]
    public async Task PagedEnvelope_HasSearchsetSelfAndOpaqueNextAndNothingElse()
    {
        Collision collision = LoadCollision();

        JsonElement bundle = await BundleAsync(Create(collision.Registry, [collision.East], next: new Uri(Next)));

        bundle.GetProperty("resourceType").GetString().ShouldBe("Bundle");
        bundle.GetProperty("type").GetString().ShouldBe("searchset");
        bundle.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["entry", "link", "resourceType", "type"]);
        Links(bundle).ShouldBe([("self", Self), ("next", Next)]);
    }

    [Fact]
    public async Task TerminalEmptyPage_HasSelfOnlyAndNoEntryArray()
    {
        Collision collision = LoadCollision();

        JsonElement bundle = await BundleAsync(Create(collision.Registry, []));

        Links(bundle).ShouldBe([("self", Self)]);
        bundle.TryGetProperty("entry", out _).ShouldBeFalse();
        bundle.TryGetProperty("total", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(int.MaxValue)]
    public async Task CountOnly_EmitsTheSuppliedTotalWithSelfOnly(int total)
    {
        Collision collision = LoadCollision();

        JsonElement bundle = await BundleAsync(Create(collision.Registry, [], total: total));

        bundle.GetProperty("total").GetInt32().ShouldBe(total);
        Links(bundle).ShouldBe([("self", Self)]);
        bundle.TryGetProperty("entry", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SuppliedTotal_AccompaniesEntriesOnAPage()
    {
        Collision collision = LoadCollision();

        JsonElement bundle = await BundleAsync(Create(collision.Registry, [collision.East, collision.West], total: 37));

        bundle.GetProperty("total").GetInt32().ShouldBe(37);
        bundle.GetProperty("entry").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void NegativeTotal_IsRejectedBeforeAnyResponseExists()
    {
        Collision collision = LoadCollision();

        Should.Throw<ArgumentOutOfRangeException>(() => Create(collision.Registry, [], total: -1));
    }

    [Fact]
    public async Task Result_IsOkFhirJsonUtf8WithTheExactBufferedLength()
    {
        Collision collision = LoadCollision();

        (DefaultHttpContext context, byte[] body) = await ExecuteAsync(Create(collision.Registry, [collision.East]));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe("application/fhir+json; charset=utf-8");
        context.Response.ContentLength.ShouldBe(body.Length);
        body.AsSpan(0, 3).SequenceEqual(Encoding.UTF8.Preamble).ShouldBeFalse();
    }

    [Fact]
    public void UnknownShard_IsRejected()
    {
        Collision collision = LoadCollision();
        ShardEntry stranger = Entry("north", "Patient", "dup", WestBytes);

        Should.Throw<ArgumentException>(() => Create(collision.Registry, [stranger]));
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public void InvalidResourceBytes_ThrowBeforeAnyResultIsReturned(string position, byte[] invalid)
    {
        Collision collision = LoadCollision();
        ShardEntry bad = Entry("west", "Observation", "bad", invalid);

        Func<IResult> create = position switch
        {
            "match" => () => Create(collision.Registry, [collision.East, bad]),
            "include" => () => Create(collision.Registry, [collision.East], includes: [collision.West, bad]),
            _ => () => Create(collision.Registry, [collision.East], includes: [collision.West], outcomes: [OutcomeBytes, invalid]),
        };

        Should.Throw<ArgumentException>(create);
    }

    public static TheoryData<string, byte[]> InvalidBodies()
    {
        var data = new TheoryData<string, byte[]>();
        data.Add("match", "{\"resourceType\":\"Observation\",\"id\":"u8.ToArray());
        data.Add("match", "[{\"resourceType\":\"Observation\"}]"u8.ToArray());
        data.Add("match", [.. "{\"a\":\""u8, 0xFF, 0xFE, .. "\"}"u8]);
        data.Add("match", [0xEF, 0xBB, 0xBF, .. "{\"a\":1}"u8]);
        data.Add("include", "{\"a\":1}{\"b\":2}"u8.ToArray());
        data.Add("outcome", "{\"a\":1} trailing"u8.ToArray());

        return data;
    }

    private static IResult CreateEligible(
        ShardRegistry registry,
        IReadOnlyList<ShardEntry> matches,
        IReadOnlyList<ShardEntry> includes,
        Func<ResourceType, bool> eligible) =>
        SearchsetResponse.Create(registry, new Uri(Self), matches, includes, [], null, new Uri(ProxyBase), eligible, null);

    private static IResult Create(
        ShardRegistry registry,
        IReadOnlyList<ShardEntry> matches,
        IReadOnlyList<ShardEntry>? includes = null,
        IReadOnlyList<byte[]>? outcomes = null,
        Uri? next = null,
        bool proxyReads = true,
        Uri? proxyReadBase = null,
        int? total = null)
    {
        return SearchsetResponse.Create(
            registry,
            new Uri(Self),
            matches,
            includes ?? [],
            [.. (outcomes ?? []).Select(o => (ReadOnlyMemory<byte>)o)],
            next,
            proxyReads ? proxyReadBase ?? new Uri(ProxyBase) : null,
            total);
    }

    private static async Task<(DefaultHttpContext Context, byte[] Body)> ExecuteAsync(IResult result)
    {
        using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
        };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return (context, body.ToArray());
    }

    private static async Task<byte[]> BodyAsync(IResult result) => (await ExecuteAsync(result)).Body;

    private static async Task<JsonElement> BundleAsync(IResult result)
    {
        byte[] body = await BodyAsync(result);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> EntriesAsync(IResult result) =>
        [.. (await BundleAsync(result)).GetProperty("entry").EnumerateArray()];

    private static string SourceShard(JsonElement entry)
    {
        JsonElement extension = entry.GetProperty("extension").EnumerateArray()
            .Single(e => e.GetProperty("url").GetString() == FederationExtensions.SourceShardUrl);
        return extension.GetProperty("valueString").GetString()!;
    }

    private static string? AlternateUrl(JsonElement entry) =>
        entry.GetProperty("link").EnumerateArray()
            .Single(l => l.GetProperty("relation").GetString() == "alternate")
            .GetProperty("url").GetString();

    private static (string Relation, string Url)[] Links(JsonElement bundle) =>
        [.. bundle.GetProperty("link").EnumerateArray().Select(l => (l.GetProperty("relation").GetString()!, l.GetProperty("url").GetString()!))];

    private static ShardEntry Entry(string shard, string type, string id, ReadOnlySpan<byte> resource) =>
        new(new ResourceKey(ShardId.Parse(shard), ResourceType.Parse(type), ResourceId.Parse(id)), null, null, resource);

    private static int IndexOf(byte[] body, byte[] slice) => body.AsSpan().IndexOf(slice);

    private static int CountOccurrences(byte[] body, byte[] slice)
    {
        int count = 0;
        for (int from = 0; from <= body.Length - slice.Length;)
        {
            int found = body.AsSpan(from).IndexOf(slice);
            if (found < 0)
            {
                break;
            }

            count++;
            from += found + slice.Length;
        }

        return count;
    }

    private static Collision LoadCollision()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(LocateManifest()));
        JsonElement root = manifest.RootElement;

        string[] aliases = ["k7q2", "p9xm"];
        var shards = new List<ShardOptions>();
        foreach (JsonElement shard in root.GetProperty("shards").EnumerateArray())
        {
            shards.Add(new ShardOptions
            {
                Id = shard.GetProperty("id").GetString(),
                Alias = aliases[shards.Count],
                BaseUrl = shard.GetProperty("baseUrl").GetString(),
                Profile = "P",
                Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.example" },
            });
        }

        var profile = new BackendProfile("P", SupportsPostSearch: true, 100, NextLinkRedemption.FollowGet, null);
        ShardRegistry registry = ShardRegistry.Create(shards, [profile]);

        Dictionary<string, string> expectedGiven = root.GetProperty("corpora").GetProperty("collision").GetProperty("expectedGiven")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value[0].GetString()!, StringComparer.Ordinal);

        return new Collision(registry, Entry("east", "Patient", "dup", EastBytes), Entry("west", "Patient", "dup", WestBytes), expectedGiven);
    }

    private static string LocateManifest()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "test", "fixtures", "federation-oracle", "manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"test/fixtures/federation-oracle/manifest.json was not found above {AppContext.BaseDirectory}.");
    }

    private sealed record Collision(ShardRegistry Registry, ShardEntry East, ShardEntry West, IReadOnlyDictionary<string, string> ExpectedGiven);
}
