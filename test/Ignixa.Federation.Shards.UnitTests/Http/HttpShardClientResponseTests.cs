using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>How a searchset becomes a <see cref="ShardPage"/>, and which searchsets are refused rather than misread.</summary>
public sealed class HttpShardClientResponseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_KeepBackendOrderIdentityVersionInstantAndExactBytes()
    {
        await using var shard = ShardHarness.Start();
        string first = "{ \"resourceType\" : \"Patient\",\"id\":\"b-2\",\"meta\":{\"versionId\":\"7\",\"lastUpdated\":\"2025-12-31T23:59:59.1234567+05:30\"},\"name\":[{\"given\":[\"Zo\\u00eb\"]}],\"active\":true }";
        string second = "{\"id\":\"a.1\",\"resourceType\":\"Patient\"}";
        string third = Searchsets.Patient("c3", versionId: "W/x", lastUpdated: "2026-01-02T03:04:05Z");
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(
            total: 3,
            entries: [Searchsets.Entry(first), Searchsets.Entry(second), Searchsets.Entry(third)])));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Entries.Select(e => e.Key).ShouldBe(
        [
            new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("b-2")),
            new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("a.1")),
            new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("c3")),
        ]);
        page.Entries.Select(e => e.VersionId).ShouldBe(["7", null, "W/x"]);
        page.Entries.Select(e => e.LastUpdated).ShouldBe(
        [
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.FromHours(5.5)).AddTicks(1234567),
            null,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        ]);
        page.Entries.Select(e => Encoding.UTF8.GetString(e.Resource.Span)).ShouldBe([first, second, third]);
        page.Total.ShouldBe(3);
        page.IsExhausted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Patient/dup")]
    [InlineData("https://other.example/fhir/Patient/other")]
    [InlineData("urn:uuid:0f2c7b2e-3a1d-4e4b-9b3e-6f1c2d3e4f50")]
    [InlineData("not a url at all")]
    public async Task FullUrl_IsNeverAuthorityAndNeverFollowed(string entryAddress)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("dup"), fullUrl: entryAddress))));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Entries.ShouldHaveSingleItem().Key.ShouldBe(new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("dup")));
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SameIdFromTwoShards_GetsEachRespondentsKey()
    {
        await using var east = ShardHarness.Start();
        await using var other = ShardHarness.Start(id: "west");
        east.Server.Respond(ScriptedResponse.Json(east.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("dup"), fullUrl: "Patient/dup"))));
        other.Server.Respond(ScriptedResponse.Json(other.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("dup"), fullUrl: "Patient/dup"))));

        ShardEntry fromEast = (await east.Client.SearchAsync(east.SarahRequest(), Ct)).Entries.Single();
        ShardEntry fromOther = (await other.Client.SearchAsync(other.SarahRequest(), Ct)).Entries.Single();

        fromEast.Key.Shard.ShouldBe(east.Shard.Id);
        fromOther.Key.Shard.ShouldBe(other.Shard.Id);
        fromEast.Key.Id.ShouldBe(fromOther.Key.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(7L)]
    public async Task Total_IsReportedAsGivenAndAbsenceStaysUnknown(long? total)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(total: total)));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Total.ShouldBe(total);
        page.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyPageWithNext_IsNotExhausted()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(next: "?ct=Mg")));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Entries.ShouldBeEmpty();
        page.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public async Task UnrelatedLinksAndUnknownMembers_AreIgnored()
    {
        await using var shard = ShardHarness.Start();
        string body = shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("p1")))
            .Replace("\"link\":[", "\"id\":\"b\",\"meta\":{\"lastUpdated\":\"x\"},\"link\":[{\"relation\":\"first\",\"url\":\"https://other.example/x\"},", StringComparison.Ordinal);
        shard.Server.Respond(ScriptedResponse.Json(body));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Entries.ShouldHaveSingleItem();
        shard.Server.Requests.Count.ShouldBe(1);
    }

    public static TheoryData<string, string> MalformedBodies => new()
    {
        { "not json", "{" },
        { "empty body", string.Empty },
        { "array root", "[]" },
        { "not a bundle", "{\"resourceType\":\"Patient\",\"id\":\"p\"}" },
        { "not a searchset", "{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}]}" },
        { "duplicate property", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}]}" },
        { "negative total", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":-1,\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}]}" },
        { "fractional total", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":1.5,\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}]}" },
        { "string total", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":\"1\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}]}" },
        { "link not array", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":{\"relation\":\"self\",\"url\":\"{self}\"}}" },
        { "two next links", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"},{\"relation\":\"next\",\"url\":\"?a=1\"},{\"relation\":\"next\",\"url\":\"?a=2\"}]}" },
        { "empty next url", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"},{\"relation\":\"next\",\"url\":\"\"}]}" },
        { "entry not array", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}],\"entry\":{}}" },
        { "include entry", Page(Searchsets.Entry(Searchsets.Patient("p1"), mode: "include")) },
        { "outcome entry", Page(Searchsets.Entry("{\"resourceType\":\"OperationOutcome\"}", mode: "outcome")) },
        { "no search mode", Page("{\"resource\":" + Searchsets.Patient("p1") + "}") },
        { "no resource", Page("{\"search\":{\"mode\":\"match\"}}") },
        { "other type", Page(Searchsets.Entry("{\"resourceType\":\"Observation\",\"id\":\"o1\"}")) },
        { "no id", Page(Searchsets.Entry("{\"resourceType\":\"Patient\"}")) },
        { "invalid id", Page(Searchsets.Entry("{\"resourceType\":\"Patient\",\"id\":\"a/b\"}")) },
        { "numeric id", Page(Searchsets.Entry("{\"resourceType\":\"Patient\",\"id\":1}")) },
        { "empty versionId", Page(Searchsets.Entry(Searchsets.Patient("p1", versionId: string.Empty))) },
        { "date-only instant", Page(Searchsets.Entry(Searchsets.Patient("p1", lastUpdated: "2026-01-02"))) },
        { "instant without zone", Page(Searchsets.Entry(Searchsets.Patient("p1", lastUpdated: "2026-01-02T03:04:05"))) },
        { "instant out of range", Page(Searchsets.Entry(Searchsets.Patient("p1", lastUpdated: "2026-13-02T03:04:05Z"))) },

        // Valid JSON text whose escaped string is not valid UTF-16: an unpaired surrogate.
        { "unpaired surrogate in id", Page(Searchsets.Entry("{\"resourceType\":\"Patient\",\"id\":\"p\\ud800\"}")) },
        { "unpaired surrogate in versionId", Page(Searchsets.Entry(Searchsets.Patient("p1", versionId: "\\udc00"))) },
        { "unpaired surrogate in lastUpdated", Page(Searchsets.Entry(Searchsets.Patient("p1", lastUpdated: "2026-01-02T03:04:05Z\\ud800"))) },
        { "unpaired surrogate in search mode", Page(Searchsets.Entry(Searchsets.Patient("p1"), mode: "match\\ud800")) },
        { "unpaired surrogate in resourceType", Page(Searchsets.Entry("{\"resourceType\":\"Patient\\ud800\",\"id\":\"p1\"}")) },
        { "unpaired surrogate in self url", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\\ud800\"}]}" },
        { "unpaired surrogate in next url", "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"},{\"relation\":\"next\",\"url\":\"?a=1\\udc00\"}]}" },
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task UnreadableOrUnrepresentableSearchset_IsMalformedNotEmpty(string defect, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(body.Replace("{self}", shard.Search + "?name=Sarah&_count=10", StringComparison.Ordinal)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse, defect);
        failure.Message.ShouldNotContain("Sarah");
        failure.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task CountOnlyWithoutTotal_IsMalformed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?name=Sarah&_count=0")));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 0, "name", "Sarah"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
    }

    [Fact]
    public async Task CountOnlyWithEntries_IsMalformed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?name=Sarah&_count=0", total: 1, entries: Searchsets.Entry(Searchsets.Patient("p1")))));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 0, "name", "Sarah"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
    }

    [Fact]
    public async Task CountOnlyWithNext_IsMalformed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?name=Sarah&_count=0", next: "?ct=Mg", total: 1)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 0, "name", "Sarah"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    private static string Page(string entry) =>
        "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":[{\"relation\":\"self\",\"url\":\"{self}\"}],\"entry\":[" + entry + "]}";
}
