using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Ignixa.Federation.Core.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Ignixa.Federation.Api.Authorization;

/// <summary>
/// The outcome of authorising one caller: either a snapshot of the resource types the caller may read, or the HTTP
/// status code that refuses the caller (design spec §10.1). <see cref="CanRead"/> is the only type-grant predicate;
/// every stage (primary search, chain sub-queries, includes, proxied reads, continuations) asks it and needs no
/// request or route knowledge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input.</b> The component never sees a raw token. Later auth wiring must authenticate the configured JWT scheme
/// explicitly (<c>HttpContext.AuthenticateAsync(scheme)</c>), with inbound claim-type mapping disabled so the Entra
/// claim names stay raw (<c>roles</c>, <c>scp</c>, <c>scope</c>), and pass that <see cref="AuthenticateResult"/> here
/// together with the scheme name taken from trusted configuration. Evaluate once per request and carry the result;
/// do not re-evaluate from claims.
/// </para>
/// <para>
/// <b>Rules.</b> A missing, failed, wrong-scheme or unauthenticated result, or a principal that is not exactly one
/// authenticated identity, is refused with 401; identities are never merged. A caller carrying any SMART scope in an
/// exact <c>scp</c> or <c>scope</c> claim is refused with 403 whatever its roles. Otherwise the readable types are
/// the union of the grants of the exact <c>roles</c> claim values, each compared ordinally and as one indivisible
/// value; no other claim grants anything. An empty union is refused with 403.
/// </para>
/// <para>
/// <b>Conservative choices.</b> SMART scope detection ignores case and splits scope claims on any whitespace, so a
/// variant such as <c>System/*.read</c> or a tab-separated list is refused rather than missed. The prefix
/// <c>launch</c> refuses every <c>launch*</c> scope, including <c>launch/patient</c>.
/// </para>
/// Instances are immutable and thread-safe.
/// </remarks>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The integration test project exercises this type; the host project has no InternalsVisibleTo.")]
public sealed class CallerAccess
{
    private const string RolesClaimType = "roles";

    private static readonly string[] ScopeClaimTypes = ["scp", "scope"];

    private static readonly string[] SmartScopePrefixes = ["patient/", "user/", "system/", "launch"];

    private static readonly CallerAccess Unauthenticated = Refused(StatusCodes.Status401Unauthorized);

    private static readonly CallerAccess Forbidden = Refused(StatusCodes.Status403Forbidden);

    private readonly FrozenSet<ResourceType> _readable;

    private CallerAccess(int? failureStatusCode, FrozenSet<ResourceType> readable)
    {
        FailureStatusCode = failureStatusCode;
        _readable = readable;
    }

    /// <summary>
    /// Gets <c>401</c> or <c>403</c> when the caller is refused, otherwise <see langword="null"/>. A refused caller
    /// can read no type.
    /// </summary>
    public int? FailureStatusCode { get; }

    /// <summary>Authorises a caller from the result of authenticating the configured JWT scheme.</summary>
    /// <param name="authentication">The result of authenticating <paramref name="federationScheme"/>, or <see langword="null"/> when none was obtained.</param>
    /// <param name="federationScheme">The trusted JWT authentication scheme name; compared ordinally with the ticket's scheme.</param>
    /// <param name="roleGrants">Trusted configuration: app role value to the resource types it may read.</param>
    /// <returns>The caller's access. Never <see langword="null"/>.</returns>
    public static CallerAccess Evaluate(
        AuthenticateResult? authentication,
        string federationScheme,
        IReadOnlyDictionary<string, IReadOnlyCollection<ResourceType>> roleGrants)
    {
        ArgumentNullException.ThrowIfNull(federationScheme);
        ArgumentNullException.ThrowIfNull(roleGrants);

        if (authentication is not { Succeeded: true, Ticket: { } ticket }
            || !string.Equals(ticket.AuthenticationScheme, federationScheme, StringComparison.Ordinal)
            || !TryGetSingleAuthenticatedIdentity(ticket.Principal, out ClaimsIdentity? identity))
        {
            return Unauthenticated;
        }

        if (CarriesSmartScope(identity))
        {
            return Forbidden;
        }

        HashSet<string> roles = identity.Claims
            .Where(c => string.Equals(c.Type, RolesClaimType, StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        // Iterate the grants rather than look roles up by key: the configured dictionary may use a case-insensitive
        // comparer, and role matching must stay ordinal.
        HashSet<ResourceType> readable = [];
        foreach ((string role, IReadOnlyCollection<ResourceType> types) in roleGrants)
        {
            if (roles.Contains(role))
            {
                readable.UnionWith(types);
            }
        }

        return readable.Count == 0 ? Forbidden : new CallerAccess(null, readable.ToFrozenSet());
    }

    /// <summary>Gets whether the caller may read all data of <paramref name="type"/>.</summary>
    /// <param name="type">The resource type, whether primary, joined, included or read directly.</param>
    /// <returns><see langword="true"/> only when the caller was not refused and a role grants the type.</returns>
    public bool CanRead(ResourceType type) => _readable.Contains(type);

    private static CallerAccess Refused(int statusCode) => new(statusCode, FrozenSet<ResourceType>.Empty);

    private static bool TryGetSingleAuthenticatedIdentity(ClaimsPrincipal principal, [NotNullWhen(true)] out ClaimsIdentity? identity)
    {
        ClaimsIdentity[] identities = [.. principal.Identities.Take(2)];
        if (identities is [{ IsAuthenticated: true } only])
        {
            identity = only;
            return true;
        }

        identity = null;
        return false;
    }

    private static bool CarriesSmartScope(ClaimsIdentity identity)
    {
        foreach (Claim claim in identity.Claims)
        {
            if (!ScopeClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (string scope in claim.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (SmartScopePrefixes.Any(prefix => scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
