using System.Text;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A <c>next</c> link is backend-controlled text. Only a continuation of this shard's own typed search is ever redeemed,
/// always at the configured base, so the credential cannot be sent to another origin, path or shard (design spec §7.4).
/// </summary>
public sealed class HttpShardClientNextLinkTests
{
    private const string NextQuery = "name=Sarah&_count=10&ct=Y3Vyc29y";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Link forms the client accepts; <c>{origin}</c>, <c>{port}</c> and <c>{q}</c> are substituted.</summary>
    public static TheoryData<string> OwnForms =>
    [
        "{origin}/fhir/Patient?{q}",
        "HTTPS://127.0.0.1:{port}/fhir/Patient?{q}",
        "https://localhost:{port}/fhir/Patient?{q}",
        "https://LOCALHOST:{port}/fhir/Patient?{q}",
        "//127.0.0.1:{port}/fhir/Patient?{q}",
        "/fhir/Patient?{q}",
        "Patient?{q}",
        "?{q}",
    ];

    /// <summary>Link forms that are another origin, path or scheme, or ambiguous; none is ever requested.</summary>
    public static TheoryData<string> ForeignForms =>
    [
        "https://127.0.0.2:{port}/fhir/Patient?{q}",
        "https://127.0.0.1:{otherPort}/fhir/Patient?{q}",
        "https://127.0.0.1/fhir/Patient?{q}",
        "https://other.example/fhir/Patient?{q}",
        "http://127.0.0.1:{port}/fhir/Patient?{q}",
        "ftp://127.0.0.1:{port}/fhir/Patient?{q}",
        "https:/fhir/Patient?{q}",
        "https:Patient?{q}",
        "https://user@127.0.0.1:{port}/fhir/Patient?{q}",
        "https://127.0.0.1%3A{port}/fhir/Patient?{q}",
        "https://127.0.0.1:0{port}/fhir/Patient?{q}",
        "https://127.0.0.1:/fhir/Patient?{q}",
        "https://127.0.0.1:{port}/fhir/Observation?{q}",
        "https://127.0.0.1:{port}/fhir/patient?{q}",
        "https://127.0.0.1:{port}/fhir/Patient/_search?{q}",
        "https://127.0.0.1:{port}/fhir/Patient/?{q}",
        "https://127.0.0.1:{port}/fhir/Pa%74ient?{q}",
        "https://127.0.0.1:{port}/fhir/x/../Patient?{q}",
        "https://127.0.0.1:{port}/fhir\\Patient?{q}",
        "https://127.0.0.1:{port}/other/Patient?{q}",
        "https://127.0.0.1:{port}/Patient?{q}",
        "https://localhost:{port}/other/Patient?{q}",
        "/other/Patient?{q}",
        "/fhir/Observation?{q}",
        "./Patient?{q}",
        "../fhir/Patient?{q}",
        "Observation?{q}",
        "fhir/Patient?{q}",
        "https://127.0.0.1:{port}/fhir/Patient",
        "https://127.0.0.1:{port}/fhir/Patient?",
        "https://127.0.0.1:{port}/fhir/Patient?{q}#fragment",
        "https://127.0.0.1:{port}/fhir/Patient?{q}&bad=a b",
        "https://127.0.0.1:{port}/fhir/Patient?{q}&bad=%zz",
        "https://127.0.0.1:{port}/fhir/Patient?{q}&bad=Zo\u00eb",
        "javascript:alert(1)",
    ];

    [Theory]
    [MemberData(nameof(OwnForms))]
    public async Task OwnLinkForm_IsKeptVerbatimAndRedeemedAtTheConfiguredBase(string form)
    {
        ArgumentNullException.ThrowIfNull(form);
        await using var shard = ShardHarness.Start();
        string link = Fill(form, shard);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(link)), ScriptedResponse.Json(Searchsets.Page(self: null)));

        ShardPage first = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        await shard.Client.ContinueAsync(first.Next!, Ct);

        first.Next!.Target.ShouldBe(link);
        shard.Server.Requests[1].Target.ShouldBe("/fhir/Patient?" + NextQuery);
    }

    [Theory]
    [MemberData(nameof(ForeignForms))]
    public async Task ForeignOrAmbiguousLink_FailsThePageAndIsNeverRequested(string form)
    {
        ArgumentNullException.ThrowIfNull(form);
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(Fill(form, shard))));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.InvalidNextLink);
        failure.Message.ShouldNotContain("Sarah");
        failure.Message.ShouldNotContain("127.0.0.1");
        shard.Server.Requests.Count.ShouldBe(1);
        shard.Credentials.Calls.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(ForeignForms))]
    public async Task ForeignOrAmbiguousCursor_IsRejectedBeforeTheCredential(string form)
    {
        await using var shard = ShardHarness.Start();

        await Should.ThrowAsync<ArgumentException>(() => shard.Client.ContinueAsync(shard.Cursor(Fill(form, shard)), Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("?name=Sarah&_count=10")]
    [InlineData("?_count=10&name=Sarah")]
    [InlineData("?name=Sarah&_count=%31%30")]
    public async Task NextRepeatingTheQueryJustSent_IsRejected(string next)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(next)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.InvalidNextLink);
    }

    [Fact]
    public async Task ContinuationNextRepeatingItsOwnQuery_IsRejected()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(self: null, next: "?ct=Mg")));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.ContinueAsync(shard.Cursor(shard.Search + "?ct=Mg"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.InvalidNextLink);
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get, NextLinkRedemption.RepostToSearch, NextLinkRedemption.FollowGet, "GET", "/fhir/Patient?ct=", "")]
    [InlineData(ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet, NextLinkRedemption.FollowGet, "GET", "/fhir/Patient?ct=", "")]
    [InlineData(ShardSearchMethod.PostSearch, NextLinkRedemption.RepostToSearch, NextLinkRedemption.RepostToSearch, "POST", "/fhir/Patient/_search", "ct=")]
    public async Task MultiHop_KeepsTheRedemptionAndSendsOnlyEachLinkQuery(
        ShardSearchMethod start,
        NextLinkRedemption profileRedemption,
        NextLinkRedemption redemption,
        string method,
        string targetPrefix,
        string bodyPrefix)
    {
        ArgumentNullException.ThrowIfNull(bodyPrefix);
        await using var shard = ShardHarness.Start(profileRedemption == NextLinkRedemption.RepostToSearch ? ShardHarness.Repost : ShardHarness.Follow);
        shard.Server.Respond(
            ScriptedResponse.Json(shard.SarahPage(next: shard.Search + "?ct=Mg", entries: Searchsets.Entry(Searchsets.Patient("p1")))),
            ScriptedResponse.Json(Searchsets.Page(self: null, next: shard.AliasBase + "/Patient?ct=Mw", entries: Searchsets.Entry(Searchsets.Patient("p2")))),
            ScriptedResponse.Json(Searchsets.Page(self: null, next: "?ct=NA", entries: Searchsets.Entry(Searchsets.Patient("p3")))),
            ScriptedResponse.Json(Searchsets.Page(self: null, entries: Searchsets.Entry(Searchsets.Patient("p4")))));

        var pages = new List<ShardPage> { await shard.Client.SearchAsync(shard.SarahRequest(start), Ct) };
        while (pages[^1].Next is { } cursor)
        {
            cursor.Redemption.ShouldBe(redemption);
            pages.Add(await shard.Client.ContinueAsync(cursor, Ct));
        }

        pages.SelectMany(p => p.Entries).Select(e => e.Key.Id.Value).ShouldBe(["p1", "p2", "p3", "p4"]);
        RecordedRequest first = shard.Server.Requests[0];
        bool post = start == ShardSearchMethod.PostSearch;
        first.Method.ShouldBe(post ? "POST" : "GET");
        first.Target.ShouldBe(post ? "/fhir/Patient/_search" : "/fhir/Patient?name=Sarah&_count=10");
        Encoding.ASCII.GetString(first.Body).ShouldBe(post ? "name=Sarah&_count=10" : string.Empty);
        RecordedRequest[] continuations = [.. shard.Server.Requests.Skip(1)];
        continuations.Length.ShouldBe(3);
        string[] tokens = ["Mg", "Mw", "NA"];
        for (int i = 0; i < continuations.Length; i++)
        {
            continuations[i].Method.ShouldBe(method);
            continuations[i].Target.ShouldBe(targetPrefix + (bodyPrefix.Length == 0 ? tokens[i] : string.Empty));
            Encoding.ASCII.GetString(continuations[i].Body).ShouldBe(bodyPrefix.Length == 0 ? string.Empty : bodyPrefix + tokens[i]);
            continuations[i].HeaderValues("Prefer").ShouldBe(["handling=strict"]);
        }
    }

    [Fact]
    public async Task CursorForAnotherShard_IsRejectedBeforeTheCredential()
    {
        await using var shard = ShardHarness.Start();
        var west = new ShardCursor(ShardId.Parse("west"), ShardHarness.Patient, shard.Search + "?ct=Mg", NextLinkRedemption.FollowGet);

        await Should.ThrowAsync<ArgumentException>(() => shard.Client.ContinueAsync(west, Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RepostCursorOnAProfileWithoutPost_IsRejectedBeforeTheCredential()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);

        await Should.ThrowAsync<ArgumentException>(
            () => shard.Client.ContinueAsync(shard.Cursor(shard.Search + "?ct=Mg", NextLinkRedemption.RepostToSearch), Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Next")]
    [InlineData("NEXT")]
    public async Task NextRelation_IsRecognizedWhateverItsCase(string relation)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Linked(("self", shard.Search + "?name=Sarah&_count=10"), (relation, "?ct=Mg"))));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.IsExhausted.ShouldBeFalse();
        page.Next.ShouldNotBeNull().Target.ShouldBe("?ct=Mg");
    }

    [Theory]
    [InlineData("self", "SELF")]
    [InlineData("Self", "self")]
    [InlineData("next", "Next")]
    public async Task RelationsDifferingOnlyInCase_AreDuplicates(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        await using var shard = ShardHarness.Start();
        string selfUrl = shard.Search + "?name=Sarah&_count=10";
        (string, string)[] links = first.Equals("self", StringComparison.OrdinalIgnoreCase)
            ? [(first, selfUrl), (second, selfUrl)]
            : [("self", selfUrl), (first, "?ct=Mg"), (second, "?ct=Mw")];
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Linked(links)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
    }

    [Fact]
    public async Task CursorFromAFirstPage_RedeemsTheSameWayAfterTheClientIsReplaced()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(next: "?ct=Mg")));
        ShardCursor cursor = (await shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)).Next!;
        var replayed = new ShardCursor(cursor.Shard, cursor.Type, cursor.Target, cursor.Redemption);
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(self: null)), ScriptedResponse.Json(Searchsets.Page(self: null)));
        using var replacement = new HttpShardClient(
            shard.Shard, shard.Credentials, TimeSpan.FromSeconds(10), 1 << 20, SyntheticLimits.Generous, meter: null, LoopbackCertificate.Trust);

        await shard.Client.ContinueAsync(cursor, Ct);
        await replacement.ContinueAsync(replayed, Ct);

        RecordedRequest[] redemptions = [.. shard.Server.Requests.Skip(1)];
        redemptions.Select(r => (r.Method, r.Target, Encoding.ASCII.GetString(r.Body)))
            .ShouldBe([("POST", "/fhir/Patient/_search", "ct=Mg"), ("POST", "/fhir/Patient/_search", "ct=Mg")]);
    }

    private static string Fill(string form, ShardHarness shard) => form
        .Replace("{origin}", shard.Server.Origin, StringComparison.Ordinal)
        .Replace("{otherPort}", (shard.Server.Port == 65535 ? 65534 : shard.Server.Port + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{port}", shard.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{q}", NextQuery, StringComparison.Ordinal);
}
