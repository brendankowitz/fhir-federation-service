using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The <c>resourceType</c> route constraint: the segment must be a lexically valid <see cref="ResourceType"/>, so
/// <c>/patient</c> and <c>/PATIENT1</c> are not search routes at all (404) rather than refused searches.
/// </summary>
internal sealed class ResourceTypeRouteConstraint : IRouteConstraint
{
    /// <summary>The constraint name used in route templates.</summary>
    public const string Name = "resourceType";

    /// <inheritdoc/>
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out object? value) && ResourceType.TryParse(value as string, out _);
}
