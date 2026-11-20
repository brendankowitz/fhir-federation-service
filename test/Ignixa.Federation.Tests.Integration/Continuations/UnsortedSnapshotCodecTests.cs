using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Continuations;

public sealed class UnsortedSnapshotCodecTests
{
    private const int ByteLimit = 4096;

    // Independent v1 wire fixtures: version, original count 3, one stream; GET a/Patient, template count 0, no occurrences.
    private const string Header = "01" + "00000003" + "00000001";
    private const string Identity = "0000000161" + "0000000750617469656E74";
    private const string Stream = Identity + "00000000" + "00" + "00000000";
    private const string Start = "00" + "0000000161";
    private const string Cursor = "02" + Identity + "000000013F" + "00"; // Opaque target "?", FollowGet.
    private const string InitialCycle = "00" + "00000001" + "00000000";
    private static readonly ShardId A = ShardId.Parse("a");
    private static readonly ShardId B = ShardId.Parse("b");
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");
    private static readonly ResourceKey Anchor = new(B, Patient, ResourceId.Parse("P.1"));
    private static readonly ParameterOccurrence[] Occurrences =
        [new("n%61me:exact", "A%2b+雪="), new("name", ""), new("n%61me:exact", "A%2b+雪=")];

    [Theory]
    [InlineData("start")]
    [InlineData("search")]
    [InlineData("cursor-initial")]
    [InlineData("cursor-follow-get")]
    [InlineData("cursor-repost")]
    public void FreshCodec_RestoresOrderedRequestsAndResumeShape(string shape)
    {
        ShardSearchRequest[] templates = [Post(B, 0), new(A, Patient, [], 27, ShardSearchMethod.Get)];
        var cycle = new CursorCycleState(0xFEDCBA9876543210UL, 8, 3);
        const string opaqueTarget = "?ct=α%2b+%FF&empty=&name=x";
        UnsortedContinuation continuation = shape switch
        {
            "start" => UnsortedContinuation.AtStart(B),
            "search" => UnsortedContinuation.AtSearch(Post(B, 2), Anchor),
            "cursor-initial" => UnsortedContinuation.AtCursor(
                new ShardCursor(B, Patient, opaqueTarget, NextLinkRedemption.FollowGet), null, CursorCycleState.Initial),
            "cursor-follow-get" => UnsortedContinuation.AtCursor(
                new ShardCursor(B, Patient, opaqueTarget, NextLinkRedemption.FollowGet), Anchor, cycle),
            "cursor-repost" => UnsortedContinuation.AtCursor(
                new ShardCursor(B, Patient, opaqueTarget, NextLinkRedemption.RepostToSearch), Anchor, cycle),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var original = new UnsortedContinuationSnapshot(templates, 3, continuation);
        templates[0] = templates[1]; // Mutating the caller's array must not mutate the snapshot.

        UnsortedContinuationSnapshot restored = Decode(Encode(original));

        restored.PageSize.ShouldBe(3);
        restored.Streams.Select(s => s.Shard).ShouldBe([B, A]);
        restored.Streams.Select(s => s.Type).ShouldBe([Patient, Patient]);
        restored.Streams.Select(s => s.PageSize).ShouldBe([0, 27]);
        restored.Streams.Select(s => s.Method).ShouldBe([ShardSearchMethod.PostSearch, ShardSearchMethod.Get]);
        restored.Streams[0].Parameters.ShouldBe(Occurrences);
        restored.Streams[1].Parameters.ShouldBeEmpty();
        restored.Continuation.Shard.ShouldBe(B);
        restored.Continuation.LastEmitted.ShouldBe(continuation.LastEmitted);
        restored.Continuation.Cursor.ShouldBe(continuation.Cursor);
        restored.Continuation.CycleState.ShouldBe(continuation.CycleState);
        if (shape == "search")
        {
            ShardSearchRequest replay = restored.Continuation.Search.ShouldNotBeNull();
            replay.PageSize.ShouldBe(2);
            replay.Method.ShouldBe(ShardSearchMethod.PostSearch);
            replay.Parameters.ShouldBe(Occurrences);
        }
        else
        {
            restored.Continuation.Search.ShouldBeNull();
        }
    }

    [Fact]
    public void Decode_AHandFramedV1Payload_PreservesAnInitialGetCursor()
    {
        UnsortedContinuationSnapshot snapshot = Decode(Convert.FromHexString(Header + Stream + Cursor + "00" + InitialCycle));

        snapshot.PageSize.ShouldBe(3);
        snapshot.Streams.ShouldHaveSingleItem().PageSize.ShouldBe(0);
        snapshot.Continuation.Cursor.ShouldBe(new ShardCursor(A, Patient, "?", NextLinkRedemption.FollowGet));
        snapshot.Continuation.CycleState.ShouldBe(CursorCycleState.Initial);
    }

    [Fact]
    public void Snapshot_RoundTripsTheMaximumAdmittedClientCount()
    {
        var stream = new ShardSearchRequest(A, Patient, [], 0, ShardSearchMethod.Get);
        var snapshot = new UnsortedContinuationSnapshot([stream], 100, UnsortedContinuation.AtStart(A));

        Decode(Encode(snapshot)).PageSize.ShouldBe(100);
    }

    [Theory]
    [InlineData("empty streams")]
    [InlineData("duplicate shard")]
    [InlineData("mixed types")]
    [InlineData("missing shard")]
    [InlineData("wrong method")]
    [InlineData("wrong predicate order")]
    [InlineData("replay too large")]
    [InlineData("wrong cursor type")]
    [InlineData("GET repost")]
    [InlineData("nonpositive page size")]
    [InlineData("page size above the admitted maximum")]
    public void Snapshot_RejectsContradictoryCoreObjects(string contradiction)
    {
        var get = new ShardSearchRequest(A, Patient, Occurrences, 0, ShardSearchMethod.Get);
        ShardSearchRequest[] streams = [get, Post(B, 1)];
        UnsortedContinuation resume = UnsortedContinuation.AtStart(B);
        int size = 3;
        switch (contradiction)
        {
            case "empty streams": streams = []; break;
            case "duplicate shard": streams[0] = streams[1]; break;
            case "mixed types":
                streams[0] = new(A, ResourceType.Parse("Observation"), [], 0, ShardSearchMethod.Get);
                break;
            case "missing shard": resume = UnsortedContinuation.AtStart(ShardId.Parse("c")); break;
            case "wrong method":
                resume = UnsortedContinuation.AtSearch(new(B, Patient, Occurrences, 1, ShardSearchMethod.Get), Anchor);
                break;
            case "wrong predicate order":
                resume = UnsortedContinuation.AtSearch(new(B, Patient, [Occurrences[1], Occurrences[0], Occurrences[2]], 1, ShardSearchMethod.PostSearch), Anchor);
                break;
            case "replay too large": resume = UnsortedContinuation.AtSearch(Post(B, 4), Anchor); break;
            case "wrong cursor type":
                resume = UnsortedContinuation.AtCursor(new ShardCursor(B, ResourceType.Parse("Observation"), "opaque", NextLinkRedemption.FollowGet), null, CursorCycleState.Initial);
                break;
            case "GET repost":
                resume = UnsortedContinuation.AtCursor(new ShardCursor(A, Patient, "opaque", NextLinkRedemption.RepostToSearch), null, CursorCycleState.Initial);
                break;
            case "nonpositive page size": size = 0; break;
            case "page size above the admitted maximum": size = 101; break;
            default: throw new ArgumentOutOfRangeException(nameof(contradiction));
        }

        Should.Throw<ArgumentException>(() => new UnsortedContinuationSnapshot(streams, size, resume))
            .Message.ShouldNotContain(Occurrences[0].Value);
    }

    [Theory]
    [InlineData("negative stream count", "0100000003FFFFFFFF")]
    [InlineData("huge stream count", "01000000037FFFFFFF")]
    [InlineData("zero client count", "010000000000000001" + Stream + Start)]
    [InlineData("client count exceeds the admitted maximum", "017FFFFFFF00000001" + Stream + Start)]
    [InlineData("unknown shape", Header + Stream + "FF")]
    [InlineData("unknown search method", Header + Identity + "00000000FF00000000" + Start)]
    [InlineData("truncated text", Header + "0000000261")]
    [InlineData("negative text length", Header + "FFFFFFFF" + Stream + Start)]
    [InlineData("huge text length", Header + "7FFFFFFF" + Stream + Start)]
    [InlineData("huge occurrence count", Header + Identity + "00000000007FFFFFFF" + Start)]
    [InlineData("invalid UTF-8", Header + "00000001FF" + "0000000750617469656E74000000000000000000" + Start)]
    [InlineData("trailing bytes", Header + Stream + Start + "00")]
    [InlineData("unknown cursor mode", Header + Stream + "02" + Identity + "000000013FFF00" + InitialCycle)]
    [InlineData("cursor type mismatch", Header + Stream + "02" + "0000000161" + "00000003546573" + "000000013F0000" + InitialCycle)]
    [InlineData("invalid anchor flag", Header + Stream + Cursor + "02" + InitialCycle)]
    [InlineData("anchor on another shard", Header + Stream + Cursor + "01" + "0000000162" + "0000000750617469656E740000000131" + InitialCycle)]
    [InlineData("anchor of another type", Header + Stream + Cursor + "01" + "0000000161" + "000000035465730000000131" + InitialCycle)]
    [InlineData("invalid fingerprint flag", Header + Stream + Cursor + "0002" + "00000001" + "00000000")]
    [InlineData("fingerprint absent at power two", Header + Stream + Cursor + "0000" + "00000002" + "00000000")]
    [InlineData("fingerprint present at initial power", Header + Stream + Cursor + "0001" + "0000000000000000" + "00000001" + "00000000")]
    [InlineData("steps outside window", Header + Stream + Cursor + "0001" + "0000000000000000" + "00000002" + "00000002")]
    [InlineData("power not a power of two", Header + Stream + Cursor + "0001" + "0000000000000000" + "00000003" + "00000000")]
    [InlineData("replay count zero", Header + Stream + "01" + Stream + Identity + "0000000131")]
    [InlineData("replay count larger than client size", Header + Stream + "01" + Identity + "000000040000000000" + Identity + "0000000131")]
    public void Decode_MalformedOrInconsistentV1_ProducesNoSnapshot(string scenario, string hex)
    {
        var codec = new UnsortedSnapshotCodec(ByteLimit);

        codec.Decode(Convert.FromHexString(hex), out UnsortedContinuationSnapshot? decoded)
            .ShouldBe(UnsortedSnapshotOutcome.Invalid, scenario);
        decoded.ShouldBeNull();
    }

    [Fact]
    public void Decode_UnknownVersion_ProducesNoSnapshot()
    {
        new UnsortedSnapshotCodec(ByteLimit).Decode([2], out UnsortedContinuationSnapshot? decoded)
            .ShouldBe(UnsortedSnapshotOutcome.UnsupportedVersion);
        decoded.ShouldBeNull();
    }

    [Fact]
    public void Codec_RequiresAPositiveBudget() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new UnsortedSnapshotCodec(0));

    [Fact]
    public void Codec_EnforcesTheSameExactByteLimitOnEncodeAndDecode()
    {
        const string multibyte = "雪";
        var template = new ShardSearchRequest(A, Patient, [new("n", multibyte)], 0, ShardSearchMethod.Get);
        var snapshot = new UnsortedContinuationSnapshot([template], 3, UnsortedContinuation.AtStart(A));
        byte[] bytes = Encode(snapshot);
        var exact = new UnsortedSnapshotCodec(bytes.Length);
        var tooSmall = new UnsortedSnapshotCodec(bytes.Length - 1);

        exact.Encode(snapshot, out byte[]? exactBytes).ShouldBe(UnsortedSnapshotOutcome.Success);
        exactBytes.ShouldBe(bytes);
        exact.Decode(bytes, out _).ShouldBe(UnsortedSnapshotOutcome.Success);
        tooSmall.Encode(snapshot, out byte[]? failedBytes).ShouldBe(UnsortedSnapshotOutcome.TooLarge);
        failedBytes.ShouldBeNull();
        tooSmall.Decode(bytes, out UnsortedContinuationSnapshot? failedSnapshot).ShouldBe(UnsortedSnapshotOutcome.TooLarge);
        failedSnapshot.ShouldBeNull();
    }

    [Fact]
    public void Encode_InvalidUtf16InOpaqueText_ProducesNoBytes()
    {
        ShardSearchRequest template = Post(B, 0);
        var cursor = new ShardCursor(B, Patient, "synthetic-\uD800", NextLinkRedemption.RepostToSearch);
        var snapshot = new UnsortedContinuationSnapshot([template], 3, UnsortedContinuation.AtCursor(cursor, null, CursorCycleState.Initial));

        new UnsortedSnapshotCodec(ByteLimit).Encode(snapshot, out byte[]? bytes).ShouldBe(UnsortedSnapshotOutcome.Invalid);
        bytes.ShouldBeNull();
    }

    private static ShardSearchRequest Post(ShardId shard, int count) =>
        new(shard, Patient, Occurrences, count, ShardSearchMethod.PostSearch);

    private static byte[] Encode(UnsortedContinuationSnapshot snapshot)
    {
        new UnsortedSnapshotCodec(ByteLimit).Encode(snapshot, out byte[]? bytes).ShouldBe(UnsortedSnapshotOutcome.Success);
        return bytes.ShouldNotBeNull();
    }

    private static UnsortedContinuationSnapshot Decode(byte[] bytes)
    {
        new UnsortedSnapshotCodec(ByteLimit).Decode(bytes, out UnsortedContinuationSnapshot? snapshot).ShouldBe(UnsortedSnapshotOutcome.Success);
        return snapshot.ShouldNotBeNull();
    }
}
