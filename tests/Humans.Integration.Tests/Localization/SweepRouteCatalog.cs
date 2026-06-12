using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Whether a page is expected to be localized (member/anonymous facing) or English-only
/// (role/admin gated). Derived from each endpoint's real authorization metadata.
/// </summary>
internal enum Audience
{
    /// <summary>Anonymous, or gated only by <see cref="PolicyNames.AppAccess"/> — should be localized.</summary>
    Public,

    /// <summary>Role- or admin-policy gated — should stay English-only.</summary>
    Restricted,
}

internal sealed record RouteCandidate(string Url, Audience Audience, string Controller, string Action);

/// <summary>
/// Builds the list of crawlable GET pages straight from the MVC action descriptors, so the page
/// inventory and the public/admin split are both authoritative (no hand-maintained list, no view
/// markers). Parameterized routes that can't be satisfied without seed data are reported as gaps.
/// </summary>
internal static class SweepRouteCatalog
{
    // Dev/operational tooling that is not a localizable member page; GET to these is noise (and a
    // GET to a seeder could mutate state), so they are excluded up front. ColorPalette is the
    // anonymous design-reference page — developer-facing and English-only by design.
    private static readonly HashSet<string> ExcludedControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "DevLogin", "DevSeed", "ColorPalette",
    };

    public static SweepCatalog Build(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IActionDescriptorCollectionProvider>();
        var crawlable = new Dictionary<string, RouteCandidate>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        foreach (var descriptor in provider.ActionDescriptors.Items)
        {
            if (descriptor is not ControllerActionDescriptor action)
                continue;
            if (ExcludedControllers.Contains(action.ControllerName))
                continue;
            if (IsApi(action) || !IsGettable(action))
                continue;

            if (!TryBuildUrl(action, out var url, out var reason))
            {
                skipped.Add($"{action.ControllerName}/{action.ActionName} — {reason}");
                continue;
            }

            var candidate = new RouteCandidate(url, Classify(action.EndpointMetadata), action.ControllerName, action.ActionName);
            crawlable.TryAdd(url, candidate); // first action wins when several map to the same URL
        }

        var ordered = crawlable.Values
            .OrderBy(c => c.Audience)
            .ThenBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SweepCatalog(ordered, skipped);
    }

    private static bool IsApi(ControllerActionDescriptor action) =>
        action.EndpointMetadata.OfType<ApiControllerAttribute>().Any();

    private static bool IsGettable(ControllerActionDescriptor action)
    {
        var methods = action.ActionConstraints?
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(c => c.HttpMethods)
            .ToList();

        // No method constraint ⇒ responds to any verb (including GET).
        return methods is null
            || methods.Count == 0
            || methods.Contains("GET", StringComparer.OrdinalIgnoreCase);
    }

    private static Audience Classify(IEnumerable<object> metadata)
    {
        if (metadata.OfType<IAllowAnonymous>().Any())
            return Audience.Public;

        var authorizeData = metadata.OfType<IAuthorizeData>().ToList();
        if (authorizeData.Count == 0)
            return Audience.Public; // no auth, or only the global "must be signed in" fallback — member facing

        foreach (var data in authorizeData)
        {
            if (!string.IsNullOrEmpty(data.Roles))
                return Audience.Restricted;
            if (!string.IsNullOrEmpty(data.Policy) && !string.Equals(data.Policy, PolicyNames.AppAccess, StringComparison.Ordinal))
                return Audience.Restricted;
        }

        return Audience.Public; // gated only by AppAccess (basic active-member gate)
    }

    private static bool TryBuildUrl(ControllerActionDescriptor action, out string url, out string reason)
    {
        url = string.Empty;
        reason = string.Empty;

        var template = action.AttributeRouteInfo?.Template;
        if (!string.IsNullOrEmpty(template))
        {
            var resolved = template
                .Replace("[controller]", action.ControllerName, StringComparison.OrdinalIgnoreCase)
                .Replace("[action]", action.ActionName, StringComparison.OrdinalIgnoreCase);

            // Optional ({x?}) and defaulted ({x=…}) segments can be omitted to form a valid URL;
            // only a required {param} genuinely needs seed data. Drop the omittable ones and skip
            // only if something required is left (e.g. /Legal from [HttpGet("{slug?}")] is scannable).
            var kept = new List<string>();
            foreach (var segment in resolved.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!segment.Contains('{'))
                {
                    kept.Add(segment);
                    continue;
                }

                var omittable = segment.Contains("?}", StringComparison.Ordinal)
                    || segment.Contains('=', StringComparison.Ordinal);
                if (!omittable)
                {
                    reason = $"param route: {template}";
                    return false;
                }
            }

            url = "/" + string.Join('/', kept);
            return true;
        }

        // Conventional route: {controller=Home}/{action=Index}/{id?}.
        url = string.Equals(action.ActionName, "Index", StringComparison.OrdinalIgnoreCase)
            ? $"/{action.ControllerName}"
            : $"/{action.ControllerName}/{action.ActionName}";
        return true;
    }
}

internal sealed record SweepCatalog(IReadOnlyList<RouteCandidate> Crawlable, IReadOnlyList<string> SkippedParamRoutes);
