using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Execution.ReverseChains;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Counts;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;
using static Ignixa.Federation.Core.UnitTests.Execution.ReverseChains.ReverseChainFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

/// <summary>Generated primary and count requests are measured in their selected method before any is sent (§7.5).</summary>
public sealed class ReverseChainTransportAdmissionTests
{
    private const string TwoClauses = Has + "=1&" + Has + "=1";

    private readonly List<ShardSearchRequest> _measured = [];

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task APrimaryRequestExactlyAtTheLimitFitsAndOneByteOverIsTooCostly(int over, bool fits)
    {
        int size = Size((await Resolve(Scripted(), Has + "=1", _ => true)).Targets.ShouldHaveSingleItem().Request(20));
        _measured.Clear();
        ReverseChainFixture fixture = Scripted();

        var resolving = Resolve(fixture, Has + "=1", r => Size(r) <= size - over);

        if (fits)
        {
            (await resolving).Targets.ShouldHaveSingleItem().Shard.ShouldBe(East);
        }
        else
        {
            (await Should.ThrowAsync<ReverseChainRefusedException>(resolving)).Reason.ShouldBe(ReverseChainRefusal.TooCostly);
        }

        Size(_measured.ShouldHaveSingleItem()).ShouldBe(size);
        fixture.Client.Calls.ShouldAllBe(c => c.Search!.Type == Observation);
    }

    [Fact]
    public async Task ALaterOwnerThatDoesNotFitRefusesTheSearchAfterEveryOwnerIsWitnessedAndBeforeAnyTargetCall()
    {
        ReverseChainFixture fixture = Scripted(westOwnsB: true);

        var exception = await Should.ThrowAsync<ReverseChainRefusedException>(
            Resolve(fixture, Has + "=1", r => r.Shard == East));

        exception.Reason.ShouldBe(ReverseChainRefusal.TooCostly);
        exception.Path.ShouldBe(fixture.Plan().Occurrences[0].Hop!.Path);
        _measured.Select(r => r.Shard).ShouldBe([East, West]);
        fixture.Client.Calls.Select(c => c.Search!.Type).ShouldBe([Observation, Observation]);
    }

    [Fact]
    public async Task AnInvalidRequestReportedByTheHostPredicatePropagatesUnchangedRatherThanAsTooCostly()
    {
        ReverseChainFixture fixture = Scripted(westOwnsB: true);
        var invalid = new ArgumentException("invalid request");

        var exception = await Should.ThrowAsync<ArgumentException>(
            Resolve(fixture, Has + "=1", r => r.Shard == East ? true : throw invalid));

        exception.ShouldBeSameAs(invalid);
        _measured.Select(r => r.Shard).ShouldBe([East, West]);
        fixture.Client.Calls.Select(c => c.Search!.Type).ShouldBe([Observation, Observation]);
    }

    [Fact]
    public async Task RawDuplicateIdsOnAPostCapableMicrosoftShardAreMeasuredAsTheGetThatPreservesThem()
    {
        var result = await Resolve(Scripted(), TwoClauses + "&_total=accurate", _ => true);

        ReverseChainTarget target = result.Targets.ShouldHaveSingleItem();
        target.Method.ShouldBe(ShardSearchMethod.Get);
        _measured.Select(r => (r.Method, r.PageSize, Wire(r))).ShouldBe(
            [(ShardSearchMethod.Get, 20, "_id=a&_id=a"), (ShardSearchMethod.Get, 0, "_id=a&_id=a")]);
    }

    [Theory]
    [InlineData("", new[] { 20 })]
    [InlineData("&_total=none", new[] { 20 })]
    [InlineData("&_count=7&_total=accurate", new[] { 7, 0 })]
    [InlineData("&_total=estimate", new[] { 20, 0 })]
    [InlineData("&_summary=count", new[] { 0 })]
    [InlineData("&_count=0", new[] { 0 })]
    public async Task OnlyTheRequestsTheSearchWillSendAreMeasured(string controls, int[] pageSizes)
    {
        await Resolve(Scripted(), Has + "=1" + controls, _ => true);

        _measured.Select(r => r.PageSize).ShouldBe(pageSizes);
        _measured.ShouldAllBe(r => r.Shard == East && r.Method == ShardSearchMethod.PostSearch && Wire(r).StartsWith("_id=a", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(0)]
    public async Task ARequestedTotalIsTooCostlyWhenEitherItsPageOrItsCountDoesNotFit(int refused)
    {
        ReverseChainFixture fixture = Scripted();

        var exception = await Should.ThrowAsync<ReverseChainRefusedException>(
            Resolve(fixture, Has + "=1&_total=accurate", r => r.PageSize != refused));

        exception.Reason.ShouldBe(ReverseChainRefusal.TooCostly);
        fixture.Client.Calls.ShouldAllBe(c => c.Search!.Type == Observation);
    }

    [Theory]
    [InlineData(CountDialect.CountZero, "_id=a&_id=a")]
    [InlineData(CountDialect.CountZeroWithSummaryCount, "_id=a&_id=a&_summary=count")]
    public async Task TheMeasuredCountIsTheRequestTheCountExecutorSendsInItsDialect(CountDialect dialect, string wire)
    {
        ReverseChainFixture fixture = Scripted(dialect: dialect);
        QueryPlan plan = fixture.Plan(TwoClauses + "&_summary=count");

        var result = await fixture.Resolver().ResolveAsync(plan, AllTypes, 100, TestContext.Current.CancellationToken);
        fixture.Client.Answer(East, 1);
        ShardCountTarget[] targets =
        [
            .. fixture.Capabilities.Registry.Shards.Select(s => result.Targets.SingleOrDefault(t => t.Shard == s.Id) is { } t
                ? ShardCountTarget.Search(t.Shard, t.Method, t.Replacements)
                : ShardCountTarget.ProvenEmpty(s.Id)),
        ];
        (await fixture.Counts!.ExecuteAsync(plan, targets, 1, TestContext.Current.CancellationToken)).ShouldBe(1);

        ShardSearchRequest measured = _measured.ShouldHaveSingleItem();
        ShardSearchRequest sent = fixture.Client.Calls.Single(c => c.Search!.Type == Patient).Search!;
        (measured.Shard, measured.Type, measured.Method, measured.PageSize, Wire(measured))
            .ShouldBe((sent.Shard, sent.Type, sent.Method, sent.PageSize, Wire(sent)));
        Wire(sent).ShouldBe(wire);
        sent.Method.ShouldBe(ShardSearchMethod.Get);
    }

    [Theory]
    [InlineData(Has + "=1&_total=accurate")]
    [InlineData("name=x&_summary=count")]
    public async Task NativePredicatesAreNotGeneratedAndSoAreNotMeasured(string query)
    {
        var fixture = new ReverseChainFixture { Capabilities = Certified(), Fits = Measure(_ => false) };

        var result = await fixture.ResolveAsync(fixture.Plan(query, native: true), budget: 0);

        result.Targets.Select(t => t.Shard).ShouldBe([East, West]);
        _measured.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmptyIntersectionSendsAndMeasuresNothing()
    {
        ReverseChainFixture fixture = Scripted();
        fixture.Client.OnSearch(East, Page(null));

        (await Resolve(fixture, Has + "=1&_total=accurate", _ => false)).Targets.ShouldBeEmpty();

        _measured.ShouldBeEmpty();
    }

    private static string Wire(ShardSearchRequest request) => QueryRewriter.Render(request.Parameters);

    /// <summary>A stand-in for the host's wire size: the encoded predicates plus the page size the client appends.</summary>
    private static int Size(ShardSearchRequest request) => Wire(request).Length + $"&_count={request.PageSize}".Length;

    /// <summary>Every count shape these tests plan, certified on east and west.</summary>
    private static SearchCapabilities Certified() =>
        CountFixture.Certified("Patient?_id=,", "Patient?_id=,&_id=,", "Patient?_has:Observation:subject:code", "Patient?name");

    private Func<ShardSearchRequest, bool> Measure(Func<ShardSearchRequest, bool> fits) => r =>
    {
        _measured.Add(r);
        return fits(r);
    };

    /// <summary>East witnesses its own Patient/a; west witnesses Patient/b on itself only when asked.</summary>
    private ReverseChainFixture Scripted(bool westOwnsB = false, CountDialect dialect = CountDialect.CountZero)
    {
        SearchCapabilities capabilities = Certified();
        var client = new FakeShardClient();
        var fixture = new ReverseChainFixture
        {
            Client = client,
            Capabilities = capabilities,
            Counts = CountFixture.Executor(client, capabilities, dialect),
            Fits = Measure(_ => true),
        };
        fixture.Client.OnSearch(East, Page(null, Obs(East, "o1", "Patient/a")));
        fixture.Client.OnSearch(West, westOwnsB ? Page(null, Obs(West, "o2", "Patient/b")) : Page(null));
        return fixture;
    }

    private Task<ReverseChainResolution> Resolve(ReverseChainFixture fixture, string query, Func<ShardSearchRequest, bool> fits) =>
        new ReverseChainFixture
        {
            Client = fixture.Client,
            Capabilities = fixture.Capabilities,
            Counts = fixture.Counts,
            Fits = Measure(fits),
        }.ResolveAsync(fixture.Plan(query));
}
