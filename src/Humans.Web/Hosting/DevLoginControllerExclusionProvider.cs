using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Hosting;

/// <summary>
/// Removes the dev-only <c>DevLoginController</c> from MVC's controller feature in Production.
/// </summary>
/// <remarks>
/// <para>
/// Its <c>DevPersonaSeeder</c> dependency is registered only outside Production, so leaving it in would fail
/// <c>ValidateOnBuild</c> at startup. Excluded here, <c>/dev/login/*</c> is a real routing 404.
/// </para>
/// <para>
/// Must run after <see cref="SectionControllerFeatureProvider"/>, which is what adds the internal controller. Resolved
/// by name and throws on a miss: a silent miss is the dev sign-in page reaching production.
/// </para>
/// </remarks>
internal sealed class DevLoginControllerExclusionProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private const string DevLoginControllerFullName = "Humans.Development.Controllers.DevLoginController";

    private static readonly Type DevLoginController =
        SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(DevLoginControllerFullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{DevLoginControllerFullName} not found in any section assembly — did the "
            + "Development section move or rename it? Production must not route /dev/login/*.");

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        for (var i = feature.Controllers.Count - 1; i >= 0; i--)
        {
            if (feature.Controllers[i].AsType() == DevLoginController)
                feature.Controllers.RemoveAt(i);
        }
    }
}
