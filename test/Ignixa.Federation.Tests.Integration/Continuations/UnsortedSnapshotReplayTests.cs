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
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "unrelated")]
    [InlineData(false, "credential")]
    [InlineData(false, "reordered")]
    [InlineData(false, "removed")]
    [InlineData(false, "moved")]
    public async Task ExecutorContinuation_ReplaysOnlyWhenParticipatingRegistryIsCurrent(bool useCursor, string registryChange)
    {
        ShardSearchRequest[] templates =
        [
            new(Second, Patient, Parameters, 0, ShardSearchMethod.PostSearch),
            new(First, Patient, [], 0, ShardSearchMethod.Get),
        ];
        ShardRegistry issuingRegistry = Registry("unchanged");
        var issuingClient = new ScriptedShard(useCursor, replaying: false);
        UnsortedPage firstPage = await ExecuteAsync(issuingClient, issuingRegistry, templates, PageSize, null);
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
        new UnsortedSnapshotCodec(ByteLimit, issuingRegistry).Encode(snapshot, out byte[]? bytes)
            .ShouldBe(UnsortedSnapshotOutcome.Success);
        using var ring = new IsolatedKeyRing();
        var clock = new SettableClock(ContinuationTestKit.Start);
        var issuer = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), clock);
        // Deliberately omit the first stream's outer dependency: the typed snapshot must still bind that stream.
        var dependencies = ContinuationDependencySet.Create([new(ShardB, "rev-b1")], []);
        string token = issuer.Issue(Caller(), dependencies, bytes.ShouldNotBeNull()).ShouldBeInline();
        var redeemer = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), clock);
        var revisions = new RevisionCatalog();
        ShardRegistry currentRegistry = Registry(registryChange);
        bool drifted = registryChange is "reordered" or "removed" or "moved";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            RedeemedContinuation redeemed = redeemer.Redeem(token, Caller(), revisions.Lookup).ShouldBeRedeemed();
            var replayingClient = new ScriptedShard(useCursor, replaying: true);
            UnsortedSnapshotOutcome outcome = new UnsortedSnapshotCodec(ByteLimit, currentRegistry)
                .Decode(redeemed.Snapshot.AsSpan(), out UnsortedContinuationSnapshot? decoded);
            UnsortedPage? resumed = null;
            if (outcome == UnsortedSnapshotOutcome.Success)
            {
                UnsortedContinuationSnapshot restored = decoded.ShouldNotBeNull();
                restored.Continuation.CycleState.ShouldBe(next.CycleState);
                resumed = await ExecuteAsync(replayingClient, currentRegistry, restored.Streams, restored.PageSize, restored.Continuation);
            }

            if (drifted)
            {
                outcome.ShouldBe(UnsortedSnapshotOutcome.Gone);
                decoded.ShouldBeNull();
                replayingClient.Calls.ShouldBe(0);
                continue;
            }

            outcome.ShouldBe(UnsortedSnapshotOutcome.Success);
            resumed.ShouldNotBeNull();
            resumed.Matches.Select(entry => entry.Key).ShouldBe([Key(Second, "tail-1"), Key(Second, "tail-2"), Key(Second, "tail-3")]);
            resumed.Next.ShouldBeNull();
            resumed.Fetches.ShouldBe(1);
        }
    }

    private static ShardRegistry Registry(string change)
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
        ShardOptions first = Shard(ShardA, "east");
        first.Auth = new AuthOptions
        {
            Type = "ClientCredentials",
            TenantId = "synthetic-tenant",
            ClientId = "synthetic-client",
            Resource = "https://fhir.example.test",
            SecretName = change == "credential" ? "ROTATED_TEST_REFERENCE" : "ORIGINAL_TEST_REFERENCE",
        };
        ShardOptions second = Shard(ShardB, "west");
        ShardOptions unrelated = Shard("other", "other");
        switch (change)
        {
            case "unrelated":
                unrelated.BaseUrl = "https://unrelated-moved.example.test/fhir";
                break;
            case "moved":
                first.BaseUrl = "https://east-moved.example.test/fhir";
                break;
        }

        ShardOptions[] shards = change switch
        {
            "reordered" => [second, first, unrelated],
            "removed" => [unrelated, second],
            _ => [first, second, unrelated],
        };
        return ShardRegistry.Create(shards, [profile]);
    }

    private static Task<UnsortedPage> ExecuteAsync(
        IShardClient client,
        ShardRegistry registry,
        IReadOnlyList<ShardSearchRequest> streams,
        int size,
        UnsortedContinuation? resume) =>
        new UnsortedPageExecutor(client, registry, maxParallelFetches: 1)
            .ExecuteAsync(streams, size, resume, fetchBudget: 10, TestContext.Current.CancellationToken);

    private static ResourceKey Key(ShardId shard, string id) => new(shard, Patient, ResourceId.Parse(id));

    private sealed class ScriptedShard(bool useCursor, bool replaying) : IShardClient
    {
        public static readonly ShardCursor Cursor = new(Second, Patient, "?ct=A%2b+%20&name=", NextLinkRedemption.RepostToSearch);

        public int Calls { get; private set; }

        public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
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

        public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken)
        {
            Calls++;
            return useCursor && cursor == Cursor
                ? Task.FromResult(MatchPage())
                : throw new InvalidOperationException("The scripted shard did not receive the exact cursor.");
        }

        private static ShardPage MatchPage() => Page(Second, null, "shared", "anchor", "tail-1", "tail-2", "tail-3");

        private static ShardPage Page(ShardId shard, ShardCursor? next, params string[] ids) =>
            new(ids.Select(id => new ShardEntry(Key(shard, id), "1", null, "{\"resourceType\":\"Patient\"}"u8)), next, null);
    }
}
