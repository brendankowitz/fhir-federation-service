using System.Buffers.Binary;
using System.Buffers.Text;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Size limits and malformed input (design spec §7.4). The inline limit is <c>MaxInlineTokenBytes</c> of serialized
/// plaintext, envelope included; a larger snapshot needs a cached reference and gets no token. Presented input is
/// bounded before it is decoded or reaches Data Protection, and anything that is not an authentic, well-formed
/// format v1 envelope is a generic invalid result.
/// </summary>
/// <remarks>
/// Tests that alter authentic contents rely on the documented format v1 prefix: version (1 byte), chain issue
/// instant and token issue instant, the two deadlines (UTC ticks, 8 bytes each, big-endian), binding hash (32 bytes), dependency count
/// (2 bytes), then the first dependency's group byte.
/// </remarks>
public sealed class ContinuationBoundsTests : IDisposable
{
    private const int ChainIssuedOffset = 1;
    private const int IssuedOffset = 9;
    private const int ChainDeadlineOffset = 17;
    private const int SlidingDeadlineOffset = 25;
    private const int DependencyCountOffset = 65;
    private const int FirstDependencyGroupOffset = 67;

    private readonly IsolatedKeyRing _ring = new();
    private readonly SettableClock _clock = new(Start);
    private readonly RevisionCatalog _revisions = new();

    public void Dispose() => _ring.Dispose();

    private (ContinuationProtector Protector, CountingDataProtectionProvider Counter) CreateProtector(TokenOptions? options = null)
    {
        var counter = new CountingDataProtectionProvider(_ring.CreateProvider());
        return (new ContinuationProtector(counter, options ?? new TokenOptions(), _clock), counter);
    }

    private IDataProtector PurposeProtector() => _ring.CreateProvider().CreateProtector(ContinuationProtector.Purpose);

    private byte[] ContentsOf(string token) => PurposeProtector().Unprotect(Base64Url.DecodeFromChars(token));

    private string Protect(byte[] contents) => Base64Url.EncodeToString(PurposeProtector().Protect(contents));

    [Theory]
    [InlineData(2048)]
    [InlineData(300)]
    public void Snapshot_ThatFillsTheInlineLimitExactly_IsIssuedInline_AndOneMoreByteNeedsACachedReference(int maxInlineTokenBytes)
    {
        (ContinuationProtector protector, CountingDataProtectionProvider counter) = CreateProtector(new TokenOptions { MaxInlineTokenBytes = maxInlineTokenBytes });
        int envelope = ContentsOf(protector.Issue(Caller(), Dependencies(), []).ShouldBeInline()).Length;
        int fits = maxInlineTokenBytes - envelope;

        string token = protector.Issue(Caller(), Dependencies(), Snapshot(fits)).ShouldBeInline();
        ContentsOf(token).Length.ShouldBe(maxInlineTokenBytes);
        protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(fits));

        int protectsBefore = counter.ProtectCalls;
        ContinuationIssue oversize = protector.Issue(Caller(), Dependencies(), Snapshot(fits + 1));
        oversize.NeedsCachedReference.ShouldBeTrue();
        oversize.InlineToken.ShouldBeNull();
        counter.ProtectCalls.ShouldBe(protectsBefore);
    }

    [Fact]
    public void Successor_WhoseSnapshotOutgrowsTheInlineLimit_NeedsACachedReference()
    {
        (ContinuationProtector protector, _) = CreateProtector();
        RedeemedContinuation redeemed = protector.Redeem(protector.Issue(Caller(), Dependencies(), Snapshot(8)).ShouldBeInline(), Caller(), _revisions.Lookup)
            .ShouldBeRedeemed();

        ContinuationIssue successor = protector.IssueSuccessor(redeemed, Snapshot(2048));

        successor.NeedsCachedReference.ShouldBeTrue();
        successor.InlineToken.ShouldBeNull();
    }

    [Fact]
    public void PresentedToken_LongerThanTheDocumentedBound_IsInvalid_WithoutReachingDataProtection()
    {
        (ContinuationProtector protector, CountingDataProtectionProvider counter) = CreateProtector();

        // (2048 plaintext + 256 protection overhead) bytes, Base64Url without padding.
        protector.MaxTokenLength.ShouldBe(3072);

        // Lengths that would otherwise decode (length % 4 != 1), so only the bound refuses them.
        protector.Redeem(new string('A', 3074), Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        protector.Redeem(new string('A', 3076), Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counter.UnprotectCalls.ShouldBe(0);

        protector.Redeem(new string('A', 3072), Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counter.UnprotectCalls.ShouldBe(1);
        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void PresentedText_ThatIsNotBase64UrlWithoutPadding_IsInvalid_WithoutReachingDataProtection()
    {
        (ContinuationProtector protector, CountingDataProtectionProvider counter) = CreateProtector();
        string token = protector.Issue(Caller(), Dependencies(), Snapshot(16)).ShouldBeInline();

        string[] malformed =
        [
            "",
            " ",
            "A",
            token + "=",
            token + "==",
            " " + token,
            token[..20] + "\n" + token[20..],
            token.Replace('-', '+').Replace('_', '/') + "+/",
            "%41%41%41%41",
        ];

        foreach (string candidate in malformed)
        {
            protector.Redeem(candidate, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        }

        counter.UnprotectCalls.ShouldBe(0);
        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void PresentedToken_WithNonZeroUnusedTrailingBits_IsInvalid()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        (ContinuationProtector protector, _) = CreateProtector();

        // Find a token whose last character carries unused bits (its length is not a multiple of four).
        int[] snapshotLengths = [16, 32, 48];
        string token = snapshotLengths
            .Select(length => protector.Issue(Caller(), Dependencies(), Snapshot(length)).ShouldBeInline())
            .First(candidate => candidate.Length % 4 != 0);

        // Flipping the lowest bit of the last character changes only unused bits: same bytes, different text.
        string nonCanonical = token[..^1] + Alphabet[Alphabet.IndexOf(token[^1], StringComparison.Ordinal) ^ 1];

        protector.Redeem(nonCanonical, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRedeemed();
    }

    [Fact]
    public void PresentedToken_ThatWasTruncatedExtendedOrAltered_IsInvalid()
    {
        (ContinuationProtector protector, _) = CreateProtector();
        string token = protector.Issue(Caller(), Dependencies(), Snapshot(16)).ShouldBeInline();
        char[] flipped = token.ToCharArray();
        flipped[token.Length / 2] = flipped[token.Length / 2] == 'A' ? 'B' : 'A';

        foreach (string candidate in new[] { token[..^1], token[..^4], token[..(token.Length / 2)], token + "AA", new string(flipped) })
        {
            protector.Redeem(candidate, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        }

        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void AuthenticContents_ThatAreNotAWellFormedEnvelope_AreInvalid()
    {
        (ContinuationProtector protector, _) = CreateProtector();
        byte[] contents = ContentsOf(protector.Issue(Caller(), Dependencies(), Snapshot(16)).ShouldBeInline());

        // Control: the unaltered contents, re-protected, redeem.
        protector.Redeem(Protect(contents), Caller(), _revisions.Lookup).ShouldBeRedeemed();
        int consultedByControl = _revisions.Consulted.Count;

        byte[][] altered =
        [
            [.. contents, 0],
            contents[..^1],
            contents[..FirstDependencyGroupOffset],
            [],
            With(contents, c => c[0] = 2),
            With(contents, c => c[0] = 0),
            With(contents, c => c[FirstDependencyGroupOffset] = 3),
            With(contents, c => c[FirstDependencyGroupOffset] = (byte)ContinuationDependencyGroup.Definition), // out of canonical order
            With(contents, c => BinaryPrimitives.WriteInt64BigEndian(c.AsSpan(ChainIssuedOffset), Start.UtcTicks + 1)),
            With(contents, c => BinaryPrimitives.WriteInt64BigEndian(c.AsSpan(ChainIssuedOffset), -1)),
            With(contents, c => BinaryPrimitives.WriteInt64BigEndian(c.AsSpan(IssuedOffset), DateTimeOffset.MaxValue.UtcTicks + 1)),
            With(contents, c => BinaryPrimitives.WriteInt64BigEndian(c.AsSpan(IssuedOffset), long.MaxValue)),
            With(contents, c => BinaryPrimitives.WriteInt64BigEndian(c.AsSpan(ChainDeadlineOffset), Start.UtcTicks)),
            With(contents, c => BinaryPrimitives.WriteInt64BigEndian(c.AsSpan(SlidingDeadlineOffset), Start.UtcTicks)),
            With(contents, c => BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(DependencyCountOffset), ushort.MaxValue)),
        ];

        foreach (byte[] candidate in altered)
        {
            protector.Redeem(Protect(candidate), Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        }

        _revisions.Consulted.Count.ShouldBe(consultedByControl);
    }

    [Fact]
    public void AuthenticContents_LargerThanTheInlineLimit_AreInvalid()
    {
        (ContinuationProtector protector, CountingDataProtectionProvider counter) = CreateProtector();

        // A well-formed envelope one byte over this instance's limit, e.g. from an instance configured with a larger one.
        (ContinuationProtector larger, _) = CreateProtector(new TokenOptions { MaxInlineTokenBytes = 4096 });
        int envelope = ContentsOf(larger.Issue(Caller(), Dependencies(), []).ShouldBeInline()).Length;
        string overLimit = larger.Issue(Caller(), Dependencies(), Snapshot(2049 - envelope)).ShouldBeInline();
        ContentsOf(overLimit).Length.ShouldBe(2049);
        larger.Redeem(overLimit, Caller(), _revisions.Lookup).ShouldBeRedeemed();
        int consultedByControl = _revisions.Consulted.Count;

        protector.Redeem(overLimit, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counter.UnprotectCalls.ShouldBe(1);
        _revisions.Consulted.Count.ShouldBe(consultedByControl);

        // Far enough over that the protected form exceeds the presented-token bound: refused before Data Protection.
        protector.Redeem(Protect(new byte[2048 + 300]), Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counter.UnprotectCalls.ShouldBe(1);
    }

    private static byte[] With(byte[] contents, Action<byte[]> change)
    {
        byte[] copy = [.. contents];
        change(copy);
        return copy;
    }
}
