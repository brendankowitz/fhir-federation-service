using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeTraversal;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>Which returned resources become includes, and when the include limit truncates them.</summary>
public sealed class RevIncludePageExecutorIncludeSetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // One identity is a key and a version, compared ordinally; a null version is its own value. The first copy's bytes
    // win across pages and across a repeated occurrence, which therefore adds nothing.
    [Fact]
    public async Task KeepsTheFirstCopyOfEachKeyAndVersion()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, East, Observation, "2");
        ShardCursor third = Cursor(registry, East, Observation, "3");
        backend.OnSearch(East, Observation, Page(second, Entry(East, Observation, "o1", "1", "first"), Entry(East, Observation, "o2", "a", "lower")));
        backend.OnContinue(
            second,
            Page(
                third,
                Entry(East, Observation, "o1", "1", "again"),
                Entry(East, Observation, "O1", "1", "upper"),
                Entry(East, Observation, "o2", "A", "upperVersion"),
                Entry(East, Observation, "o3", "1", "one")));
        backend.OnContinue(third, Page(null, Entry(East, Observation, "o1", "2", "two"), Entry(East, Observation, "o3", null, "none")));
        backend.OnSearch(West, Observation, Page(null, Entry(West, Observation, "o1", "1", "west")));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, $"{Observations}&{Observations}"), [Match(East, "a")], ReadAll, 20, 8, Token);

        page.Rendered().ShouldBe(
        [
            "east/Observation/o1/1/first",
            "east/Observation/o2/a/lower",
            "east/Observation/O1/1/upper",
            "east/Observation/o2/A/upperVersion",
            "east/Observation/o3/1/one",
            "east/Observation/o1/2/two",
            "east/Observation/o3/-/none",
            "west/Observation/o1/1/west",
        ]);
        page.Truncated.ShouldBeFalse();
        page.Fetches.ShouldBe(8);
    }

    // Organization:partof searches the page's own type, so a match can come back as a source: the match wins by its key
    // whatever version the source copy has, and a suppressed copy neither fills the limit nor truncates it.
    [Fact]
    public async Task AnEmittedMatchIsNeverAlsoAnInclude()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(
            East,
            Organization,
            Page(
                null,
                Entry(East, Organization, "parent", "7", "self"),
                Entry(East, Organization, "child", "1", "child"),
                Entry(East, Organization, "sibling", "1", "self")));
        backend.OnSearch(West, Organization, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry).ExecuteAsync(
            Plan(registry, "_revinclude=Organization:partof", "Organization"),
            [Match(East, "parent", Organization), Match(East, "sibling", Organization)],
            ReadAll,
            1,
            2,
            Token);

        page.Rendered().ShouldBe(["east/Organization/child/1/child"]);
        page.Truncated.ShouldBeFalse();
    }

    // Filling the limit exactly is not truncation: later pages are still read for a novel resource, and empty or
    // already-held ones do not truncate.
    [Fact]
    public async Task FillingTheLimitExactlyIsNotTruncation()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, East, Observation, "2");
        ShardCursor third = Cursor(registry, East, Observation, "3");
        backend.OnSearch(East, Observation, Page(second, Entry(East, Observation, "o1"), Entry(East, Observation, "o2")));
        backend.OnContinue(second, Page(third));
        backend.OnContinue(third, Page(null, Entry(East, Observation, "o2"), Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 2, 4, Token);

        page.Rendered().ShouldBe(["east/Observation/o1/1/", "east/Observation/o2/1/"]);
        page.Truncated.ShouldBeFalse();
        page.Fetches.ShouldBe(4);
    }

    // The first novel resource beyond the limit truncates: no cursor is followed after it (none is scripted), yet every
    // remaining first page is still searched and validated.
    [Fact]
    public async Task ANovelResourceBeyondTheLimitTruncatesAndStopsFollowingCursors()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(
            East,
            Observation,
            Page(Cursor(registry, East, Observation, "2"), Entry(East, Observation, "o1"), Entry(East, Observation, "o2"), Entry(East, Observation, "o3")));
        backend.OnSearch(West, Observation, Page(Cursor(registry, West, Observation, "2"), Entry(West, Observation, "w1")));
        backend.OnSearch(East, Encounter, Page(null, Entry(East, Encounter, "e1")));
        backend.OnSearch(West, Encounter, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, 2, 10, Token);

        page.Rendered().ShouldBe(["east/Observation/o1/1/", "east/Observation/o2/1/"]);
        page.Truncated.ShouldBeTrue();
        page.Fetches.ShouldBe(4);
        backend.Client.Calls.ShouldAllBe(call => call.Search != null);
    }

    [Fact]
    public async Task AFailedFirstPageAfterTruncationFailsTheWholeCall()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null, Entry(East, Observation, "o1"), Entry(East, Observation, "o2")));
        backend.OnSearch(West, Observation, Page(null));
        backend.OnSearch(East, Encounter, Page(null));
        backend.OnSearchThrows(West, Encounter, new InvalidOperationException("west is down"));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, 1, 10, Token));

        thrown.Shard.ShouldBe(West);
        thrown.Reason.ShouldBe(ShardFailureReason.Failed);
        backend.Client.Calls.Count.ShouldBe(4);
    }

    // A zero limit asks whether any include exists: a novel resource truncates, and none (after every page) does not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AZeroLimitReportsOnlyWhetherAnIncludeExists(bool exists)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, East, Observation, "2");
        backend.OnSearch(East, Observation, exists ? Page(second, Entry(East, Observation, "o1")) : Page(second));
        backend.OnContinue(second, Page(null));
        backend.OnSearch(West, Observation, Page(null));

        RevIncludePage page = await Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 0, 3, Token);

        page.Includes.ShouldBeEmpty();
        page.Truncated.ShouldBe(exists);
        page.Fetches.ShouldBe(exists ? 2 : 3);
    }
}
