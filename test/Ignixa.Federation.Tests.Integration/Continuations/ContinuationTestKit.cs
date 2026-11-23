using Ignixa.Federation.Api.Continuations;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>Synthetic callers, dependencies and snapshots shared by the continuation tests. No real identity or data.</summary>
internal static class ContinuationTestKit
{
    public const string Issuer = "https://login.example.test/tenant-a/v2.0";
    public const string Subject = "subject-a";
    public const string ObjectId = "object-a";

    public const string ShardA = "shard-a";
    public const string ShardB = "shard-b";
    public const string CodeDefinition = "http://hl7.org/fhir/SearchParameter/clinical-code";

    public static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public static ContinuationBinding Caller(params string[] roles) =>
        ContinuationBinding.Create(Issuer, Subject, ObjectId, clientId: null, roles.Length == 0 ? ["Reader", "Auditor"] : roles);

    /// <summary>Shards A and B participate and the clinical code definition was resolved.</summary>
    public static ContinuationDependencySet Dependencies() =>
        ContinuationDependencySet.Create(
            [new(ShardA, "rev-a1"), new(ShardB, "rev-b1")],
            [new(CodeDefinition, "def-1")]);

    /// <summary>A deterministic stand-in for serialized plan and replay bytes.</summary>
    public static byte[] Snapshot(int length, byte seed = 7) =>
        [.. Enumerable.Range(0, length).Select(i => unchecked((byte)(seed + (i * 31))))];

    public static string ShouldBeInline(this ContinuationIssue issue)
    {
        issue.NeedsCachedReference.ShouldBeFalse();
        return issue.InlineToken.ShouldNotBeNull();
    }

    public static RedeemedContinuation ShouldBeRedeemed(this ContinuationRedemption redemption)
    {
        redemption.Outcome.ShouldBe(ContinuationRedemptionOutcome.Redeemed);
        return redemption.Continuation.ShouldNotBeNull();
    }

    public static void ShouldBeRejected(this ContinuationRedemption redemption, ContinuationRedemptionOutcome expected)
    {
        expected.ShouldNotBe(ContinuationRedemptionOutcome.Redeemed);
        redemption.Outcome.ShouldBe(expected);
        redemption.IsRedeemed.ShouldBeFalse();
        redemption.Continuation.ShouldBeNull();
    }

    public static string ShouldBeIssued(this ContinuationIssuance issuance)
    {
        issuance.Outcome.ShouldBe(ContinuationIssuanceOutcome.Issued);
        issuance.IsIssued.ShouldBeTrue();
        return issuance.Token.ShouldNotBeNull();
    }

    public static void ShouldBeRefused(this ContinuationIssuance issuance, ContinuationIssuanceOutcome expected)
    {
        expected.ShouldNotBe(ContinuationIssuanceOutcome.Issued);
        issuance.Outcome.ShouldBe(expected);
        issuance.IsIssued.ShouldBeFalse();
        issuance.Token.ShouldBeNull();
    }

    /// <summary>Whether a token is a cached reference rather than an inline token.</summary>
    public static bool IsReference(string token) => token.StartsWith(ContinuationTokenService.ReferencePrefix, StringComparison.Ordinal);

    /// <summary>Asserts the task ends in cancellation (<see cref="OperationCanceledException"/> or a subtype).</summary>
    public static async Task ShouldBeCanceledAsync(this Task task)
    {
        Exception? thrown = await Record(task);
        thrown.ShouldNotBeNull("the task completed instead of being canceled");
        thrown.ShouldBeAssignableTo<OperationCanceledException>();
    }

    private static async Task<Exception?> Record(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (OperationCanceledException cancelled)
        {
            return cancelled;
        }
    }
}
