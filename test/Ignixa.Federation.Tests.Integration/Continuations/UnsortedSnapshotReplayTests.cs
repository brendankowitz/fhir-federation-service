using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

public sealed class UnsortedSnapshotReplayTests
{
    private const int ByteLimit = 4096;
    private const int PageSize = 3;
    private static readonly ShardId First = ShardId.Parse(ShardA);
    private static readonly ShardId Second = ShardId.Parse(ShardB);
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");
    private static readonly ParameterOccurrence[] Parameters =
        [new("n%61me:exact", "A%2b+%20"), new("name", ""), new("n%61me:exact", "A%2b+%20")];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorContinuation_ProtectsAndReplaysAcrossFreshInstances(bool useCursor)
    {
        ShardSearchRequest[] templates =
        [
            new(Second, Patient, Parameters, 0, ShardSearchMethod.PostSearch),
            new(First, Patient, [], 0, ShardSearchMethod.Get),
        ];
        var issuingClient = new ScriptedShard(useCursor, replaying: false);
        UnsortedPage firstPage = await ExecuteAsync(issuingClient, templates, PageSize, null);
        firstPage.Matches.Select(entry => entry.Key).ShouldBe([Key(First, "shared"), Key(Second, "shared"), Key(Second, "anchor")]);
        UnsortedContinuation next = firstPage.Next.ShouldNotBeNull();
        next.LastEmitted.ShouldBe(Key(Second, "anchor"));
        if (useCursor)
        {
            next.Cursor.ShouldBe(ScriptedShard.Cursor);
            next.CycleState.SavedFingerprint.ShouldNotBeNull();
        }
        else
        {
            next.Search.ShouldNotBeNull().PageSize.ShouldBe(2);
        }

        var snapshot = new UnsortedContinuationSnapshot(templates, PageSize, next);
        new UnsortedSnapshotCodec(ByteLimit).Encode(snapshot, out byte[]? bytes).ShouldBe(UnsortedSnapshotOutcome.Success);
        using var ring = new IsolatedKeyRing();
        var clock = new SettableClock(ContinuationTestKit.Start);
        var issuer = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), clock);
        string token = issuer.Issue(Caller(), Dependencies(), bytes.ShouldNotBeNull()).ShouldBeInline();
        var redeemer = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), clock);
        var revisions = new RevisionCatalog();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            RedeemedContinuation redeemed = redeemer.Redeem(token, Caller(), revisions.Lookup).ShouldBeRedeemed();
            new UnsortedSnapshotCodec(ByteLimit).Decode(redeemed.Snapshot.AsSpan(), out UnsortedContinuationSnapshot? decoded)
                .ShouldBe(UnsortedSnapshotOutcome.Success);
            UnsortedContinuationSnapshot restored = decoded.ShouldNotBeNull();
            restored.Continuation.CycleState.ShouldBe(next.CycleState);

            var replayingClient = new ScriptedShard(useCursor, replaying: true);
            UnsortedPage resumed = await ExecuteAsync(replayingClient, restored.Streams, restored.PageSize, restored.Continuation);

            resumed.Matches.Select(entry => entry.Key).ShouldBe([Key(Second, "tail-1"), Key(Second, "tail-2"), Key(Second, "tail-3")]);
            resumed.Next.ShouldBeNull();
            resumed.Fetches.ShouldBe(1);
        }
    }

    private static Task<UnsortedPage> ExecuteAsync(
        IShardClient client,
        IReadOnlyList<ShardSearchRequest> streams,
        int size,
        UnsortedContinuation? resume)
    {
        static ShardOptions Shard(string id, string alias) => new()
        {
            Id = id,
            Alias = alias,
            BaseUrl = $"https://{id}.example.test/fhir",
            Profile = "Synthetic",
            Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.example.test" },
        };

        var profile = new BackendProfile("Synthetic", SupportsPostSearch: true, 10, NextLinkRedemption.RepostToSearch, null);
        ShardRegistry registry = ShardRegistry.Create([Shard(ShardA, "east"), Shard(ShardB, "west")], [profile]);
        return new UnsortedPageExecutor(client, registry, maxParallelFetches: 1)
            .ExecuteAsync(streams, size, resume, fetchBudget: 10, TestContext.Current.CancellationToken);
    }

    private static ResourceKey Key(ShardId shard, string id) => new(shard, Patient, ResourceId.Parse(id));

    private sealed class ScriptedShard(bool useCursor, bool replaying) : IShardClient
    {
        public static readonly ShardCursor Cursor = new(Second, Patient, "?ct=A%2b+%20&name=", NextLinkRedemption.RepostToSearch);

        public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken)
        {
            if (request.Shard == First && !replaying)
            {
                return Task.FromResult(Page(First, null, "shared"));
            }

            if (request.Shard != Second || (replaying && useCursor) || request.Type != Patient
                || request.Method != ShardSearchMethod.PostSearch || request.PageSize != 2
                || !request.Parameters.SequenceEqual(Parameters))
            {
                throw new InvalidOperationException("The scripted shard did not receive the exact search request.");
            }

            return Task.FromResult(useCursor ? Page(Second, Cursor) : MatchPage());
        }

        public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken) =>
            useCursor && cursor == Cursor
                ? Task.FromResult(MatchPage())
                : throw new InvalidOperationException("The scripted shard did not receive the exact cursor.");

        private static ShardPage MatchPage() => Page(Second, null, "shared", "anchor", "tail-1", "tail-2", "tail-3");

        private static ShardPage Page(ShardId shard, ShardCursor? next, params string[] ids) =>
            new(ids.Select(id => new ShardEntry(Key(shard, id), "1", null, "{\"resourceType\":\"Patient\"}"u8)), next, null);
    }
}
