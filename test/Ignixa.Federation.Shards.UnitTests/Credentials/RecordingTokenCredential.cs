using Azure;
using Azure.Core;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// A synthetic SDK credential: it records the scopes it was asked for and never contacts an identity service. By default
/// it issues the token <see cref="Token"/>, which is unique to this instance.
/// </summary>
internal sealed class RecordingTokenCredential : TokenCredential
{
    private readonly List<string[]> _scopeRequests = [];

    public RecordingTokenCredential(int id)
    {
        Token = $"synthetic.token.{id}";
        Acquire = (_, _) => ValueTask.FromResult(new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1)));
    }

    public string Token { get; }

    /// <summary>Gets or sets what an acquisition does; by default it returns <see cref="Token"/>.</summary>
    public Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> Acquire { get; set; }

    public IReadOnlyList<string[]> ScopeRequests
    {
        get
        {
            lock (_scopeRequests)
            {
                return [.. _scopeRequests];
            }
        }
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The provider acquires tokens asynchronously.");

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (_scopeRequests)
        {
            _scopeRequests.Add([.. requestContext.Scopes]);
        }

        return Acquire(requestContext, cancellationToken);
    }
}
