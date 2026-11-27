using System.Globalization;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;

namespace Humans.Base.Hosting;

/// <summary>
/// Which section serves a link, read off the routed MVC actions, so a view's link into a switched-off section can be
/// dropped without anyone writing the section's name beside it.
/// </summary>
/// <remarks>
/// Covers exactly what <c>SectionSwitchFilter</c> answers 404 for: controller actions. Minimal-API endpoints and
/// static files map to no section.
/// </remarks>
public sealed class SectionRoutes(EndpointDataSource endpoints)
{
    /// <summary>Lazy because endpoints are only mapped after the container is built.</summary>
    private readonly Lazy<Map> _map = new(() => Map.Build(endpoints.Endpoints));

    /// <summary>Null for an unknown controller, and for a name two sections both use.</summary>
    public string? SectionOfController(string controller) => _map.Value.ByController.GetValueOrDefault(controller);

    /// <summary>
    /// The section whose action a local URL routes to; null for anything else. Route constraints and HTTP methods are
    /// not evaluated, so where templates overlap the most specific one decides.
    /// </summary>
    public string? SectionOfUrl(string? url, PathString pathBase = default)
    {
        if (url is null || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal))
            return null;

        var end = url.AsSpan().IndexOfAny('?', '#');
        var path = new PathString(end < 0 ? url : url[..end]);
        if (pathBase.HasValue && path.StartsWithSegments(pathBase, StringComparison.OrdinalIgnoreCase, out var remaining))
            path = remaining.HasValue ? remaining : "/";

        return _map.Value.SectionOf(path);
    }

    private sealed record Route(TemplateMatcher Matcher, RoutePattern Pattern, int Order, string Section)
    {
        public bool Matches(PathString path)
        {
            var values = new RouteValueDictionary();
            if (!Matcher.TryMatch(path, values))
                return false;

            // A conventional route keeps {controller}/{action} as parameters and names its action in RequiredValues.
            foreach (var (key, required) in Pattern.RequiredValues)
            {
                if (Pattern.GetParameter(key) is not null && !string.Equals(
                        Convert.ToString(values[key], CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(required, CultureInfo.InvariantCulture) ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }

    /// <summary>Routes bucketed by the first path segment they can only match, so a lookup tries a handful.</summary>
    private sealed record Map(
        Dictionary<string, string?> ByController,
        Dictionary<string, List<Route>> ByFirstSegment,
        List<Route> AnyFirstSegment)
    {
        public static Map Build(IReadOnlyList<Endpoint> endpoints)
        {
            var map = new Map(
                new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), []);

            foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
            {
                if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action)
                    continue;

                var section = SectionSwitchboard.SectionNameOf(action.ControllerTypeInfo);
                map.ByController[action.ControllerName] =
                    map.ByController.TryGetValue(action.ControllerName, out var known)
                    && !string.Equals(known, section, StringComparison.Ordinal)
                        ? null
                        : section;

                var pattern = endpoint.RoutePattern;
                var route = new Route(
                    new TemplateMatcher(new RouteTemplate(pattern), new RouteValueDictionary(pattern.Defaults)),
                    pattern, endpoint.Order, section);

                if (FirstSegment(pattern) is { } first)
                {
                    if (!map.ByFirstSegment.TryGetValue(first, out var bucket))
                        map.ByFirstSegment[first] = bucket = [];
                    bucket.Add(route);
                }
                else
                {
                    map.AnyFirstSegment.Add(route);
                }
            }

            return map;
        }

        public string? SectionOf(PathString path)
        {
            var value = path.Value.AsSpan(1);
            var slash = value.IndexOf('/');
            var first = (slash < 0 ? value : value[..slash]).ToString();

            return ByFirstSegment.GetValueOrDefault(first, []).Concat(AnyFirstSegment)
                .Where(route => route.Matches(path))
                .OrderBy(route => route.Order)
                .ThenBy(route => route.Pattern.InboundPrecedence)
                .FirstOrDefault()?.Section;
        }

        /// <summary>Null when the first segment can vary or be left out.</summary>
        private static string? FirstSegment(RoutePattern pattern)
        {
            if (pattern.PathSegments.Count == 0)
                return string.Empty;
            if (pattern.PathSegments[0] is not { IsSimple: true } segment)
                return null;

            return segment.Parts[0] switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternParameterPart { IsOptional: false, IsCatchAll: false } parameter
                    when !pattern.Defaults.ContainsKey(parameter.Name)
                    && pattern.RequiredValues.TryGetValue(parameter.Name, out var required)
                    && required is string { Length: > 0 } name => name,
                _ => null,
            };
        }
    }
}
