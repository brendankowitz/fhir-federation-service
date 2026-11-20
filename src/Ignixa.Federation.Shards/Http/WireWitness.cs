namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Counts send attempts in one call and remembers its most recent HTTP status. An exception alone cannot show whether
/// the first attempt returned a given status, because the credential provider and the meter may throw a
/// <see cref="ShardCallException"/> of their own and a retry may end in a status the first attempt never got.
/// </summary>
/// <remarks>Single call, single thread of control (the attempts of one call are sequential). Holds no request or response data.</remarks>
internal sealed class WireWitness
{
    private int _sends;
    private int _lastStatus;

    internal bool WasOnlyAnsweredWith(int status) => _sends == 1 && _lastStatus == status;

    internal void Sending() => _sends++;

    internal void Answered(int status) => _lastStatus = status;
}
