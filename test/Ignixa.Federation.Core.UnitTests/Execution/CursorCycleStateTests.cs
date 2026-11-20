using Ignixa.Federation.Core.Execution;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>The carried cycle-detection state is valid by construction and never prints its fingerprint.</summary>
public sealed class CursorCycleStateTests
{
    [Fact]
    public void Initial_HasSeenNoCursor()
    {
        CursorCycleState.Initial.SavedFingerprint.ShouldBeNull();
        CursorCycleState.Initial.Power.ShouldBe(1);
        CursorCycleState.Initial.Steps.ShouldBe(0);
        new CursorCycleState(null, 1, 0).ShouldBe(CursorCycleState.Initial);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(8, 7)]
    [InlineData(1 << 30, (1 << 30) - 1)]
    public void Constructor_RebuildsAnyReachableState(int power, int steps)
    {
        var state = new CursorCycleState(42UL, power, steps);

        state.SavedFingerprint.ShouldBe(42UL);
        state.Power.ShouldBe(power);
        state.Steps.ShouldBe(steps);
        state.ShouldBe(new CursorCycleState(42UL, power, steps));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-2, 0)]
    [InlineData(3, 0)]
    [InlineData(2, 2)]
    [InlineData(2, -1)]
    [InlineData(4, 5)]
    public void Constructor_RejectsAPowerOrStepCountBrentsAlgorithmCannotReach(int power, int steps)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CursorCycleState(42UL, power, steps));
    }

    [Fact]
    public void Constructor_RequiresASavedFingerprintExactlyWhenACursorWasSeen()
    {
        Should.Throw<ArgumentException>(() => new CursorCycleState(null, 2, 0));
        Should.Throw<ArgumentException>(() => new CursorCycleState(42UL, 1, 0));
    }

    [Fact]
    public void ToString_LeavesOutTheFingerprint()
    {
        const ulong Fingerprint = 0xDEADBEEFCAFEF00D;

        string text = new CursorCycleState(Fingerprint, 4, 3).ToString();

        text.ShouldNotContain(Fingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture));
        text.ShouldNotContain("DEADBEEF", Case.Insensitive);
        text.ShouldContain("4");
        text.ShouldContain("3");
    }
}
