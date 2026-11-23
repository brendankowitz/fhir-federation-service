using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeTraversal;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>Which reverse include searches <see cref="RevIncludePageExecutor"/> sends for a page, and exactly how.</summary>
public sealed class RevIncludePageExecutorSearchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // East-only targets still search west, whose Observations may point at east Patients; each shard gets its own spelling,
    // method and clamped count, and the client receives the very request object the fit check measured.
    [Fact]
    public async Task SearchesEveryShardForTheEmittedTargetsWithTheFittedRequestsUnchanged()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null));
        backend.OnSearch(West, Observation, Page(null));
        var fitted = new List<ShardSearchRequest>();

        RevIncludePage page = await Executor(backend.Client, registry, fits: request => { fitted.Add(request); return true; })
            .ExecuteAsync(Plan(registry, $"_count=5&{Observations}"), [Match(East, "a"), Match(East, "b"), Match(East, "a")], ReadAll, 10, 10, Token);

        ShardSearchRequest[] sent = [.. backend.Client.Calls.Select(call => call.Search!)];
        sent.Length.ShouldBe(2);
        sent[0].ShouldBeSameAs(fitted[0]);
        sent[1].ShouldBeSameAs(fitted[1]);
        sent.Select(r => (r.Shard, r.Type, r.Method, r.PageSize)).ShouldBe(
        [
            (East, Observation, ShardSearchMethod.PostSearch, 5),
            (West, Observation, ShardSearchMethod.Get, 3),
        ]);
        sent[0].Decoded().Split(',').ShouldBe(["Patient/a", "https://east.example/fhir/Patient/a", "Patient/b", "https://east.example/fhir/Patient/b"]);
        sent[1].Decoded().Split(',').ShouldBe(["https://east.example/fhir/Patient/a", "https://east.example/fhir/Patient/b"]);
        page.Includes.ShouldBeEmpty();
        page.OmittedUnauthorized.ShouldBeFalse();
        page.Truncated.ShouldBeFalse();
        page.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task RunsEveryReverseIncludeInPlanOrderAndKeepsTheirIncludesInThatOrder()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null, Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, Page(null, Entry(West, Observation, "w1")));
        backend.OnSearch(East, Encounter, Page(null, Entry(East, Encounter, "e1")));
        backend.OnSearch(West, Encounter, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, $"{Encounters}&{Observations}"), [Match(West, "p")], ReadAll, 10, 4, Token);

        backend.Client.Calls.Select(call => (call.Shard, call.Search!.Type)).ShouldBe(
            [(East, Encounter), (West, Encounter), (East, Observation), (West, Observation)]);
        page.Rendered().ShouldBe(["east/Encounter/e1/1/", "east/Observation/o1/1/", "west/Observation/w1/1/"]);
        page.Truncated.ShouldBeFalse();
        page.Fetches.ShouldBe(4);
    }

    // The caller may read Observations but not Encounters: the Encounter occurrence is never compiled, fitted or searched.
    [Fact]
    public async Task LeavesOutAForbiddenSourceWithoutFittingOrSearchingIt()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null, Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, Page(null));
        var fitted = new List<ShardSearchRequest>();

        RevIncludePage page = await Executor(backend.Client, registry, fits: request => { fitted.Add(request); return true; })
            .ExecuteAsync(Plan(registry, $"{Encounters}&{Observations}"), [Match(East, "a")], Readable(Patient, Observation), 10, 2, Token);

        fitted.ShouldAllBe(request => request.Type == Observation);
        backend.Client.Calls.ShouldAllBe(call => call.Search!.Type == Observation);
        page.Rendered().ShouldBe(["east/Observation/o1/1/"]);
        page.OmittedUnauthorized.ShouldBeTrue();
        page.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task IgnoresForwardIncludes()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null));
        backend.OnSearch(West, Observation, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, $"_include=Patient:organization&{Observations}"), [Match(East, "a")], ReadAll, 10, 2, Token);

        backend.Client.Calls.ShouldAllBe(call => call.Search!.Type == Observation);
        page.Fetches.ShouldBe(2);
    }

    // The planner keeps a repeated _revinclude; each occurrence is searched on its own, never coalesced.
    [Fact]
    public async Task SearchesARepeatedReverseIncludeAgain()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null, Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, $"{Observations}&{Observations}"), [Match(East, "a")], ReadAll, 10, 4, Token);

        backend.Client.Calls.Select(call => call.Shard).ShouldBe([East, West, East, West]);
        page.Rendered().ShouldBe(["east/Observation/o1/1/"]);
        page.Fetches.ShouldBe(4);
    }
}
