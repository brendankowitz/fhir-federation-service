using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Api.Reads;

/// <summary>
/// The <c>resourceId</c> route constraint: the segment must be a lexically valid <see cref="ResourceId"/>, so
/// <c>/Patient/_search</c> and <c>/Patient/_history</c> are not typed-read routes at all.
/// </summary>
internal sealed class ResourceIdRouteConstraint : IRouteConstraint
{
    /// <summary>The constraint name used in route templates.</summary>
    public const string Name = "resourceId";

    /// <inheritdoc/>
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out object? value) && ResourceId.TryParse(value as string, out _);
}
