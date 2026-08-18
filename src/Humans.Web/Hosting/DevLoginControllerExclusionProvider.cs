using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Hosting;

/// <summary>
/// Removes the dev-only <c>DevLoginController</c> from MVC's controller feature when running
/// in Production.
/// </summary>
/// <remarks>
/// <para>
/// <c>DevLoginController</c> depends on <c>DevPersonaSeeder</c>, which the Development
/// section's <c>Section.Register</c> only registers outside Production. With
/// <c>ValidateOnBuild = true</c> / <c>ValidateScopes = true</c>, leaving the controller
/// in the MVC graph in Production would either:
/// </para>
/// <list type="bullet">
///   <item><description>Fail host startup because DI cannot resolve <c>DevPersonaSeeder</c>.</description></item>
///   <item><description>500 on any <c>/dev/login/*</c> request before the controller's
///   <c>IsDevAuthEnabled()</c> guard can short-circuit to <c>NotFound</c>.</description></item>
/// </list>
/// <para>
/// Excluding the controller at the feature-provider level means it doesn't exist as far
/// as MVC is concerned in Production: routes never bind, DI never inspects its
/// constructor, and <c>/dev/login/*</c> returns a real 404 from the routing layer.
/// </para>
/// <para>
/// This is implemented as an <see cref="IApplicationFeatureProvider{ControllerFeature}"/>
/// that runs after the default <see cref="ControllerFeatureProvider"/> — and after
/// <see cref="SectionControllerFeatureProvider"/>, which is what puts the internal section
/// controller in the list at all — and removes the dev controller from it. Wired up via
/// <c>ConfigureApplicationPartManager</c> on the <c>IMvcBuilder</c> returned by
/// <c>AddControllersWithViews</c>, only when <c>IsProduction()</c> is true.
/// </para>
/// <para>
/// The controller moved into <c>src/Sections/Humans.Development</c> at G5
/// (nobodies-collective/Humans#866) and is <c>internal</c> there, so this cannot be a
/// <c>typeof</c> any more. Resolving by name through
/// <see cref="SectionDiscoveryExtensions.SectionAssemblies"/> is the same mechanism the
/// architecture sweeps use, and it throws on a miss for the same reason: a silent miss here
/// is the dev sign-in page reaching production.
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
