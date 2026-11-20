namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>A wall clock that reads exactly what the test last set.</summary>
internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
