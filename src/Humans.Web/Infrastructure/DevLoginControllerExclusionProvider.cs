using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Removes the dev-only <see cref="Controllers.DevLoginController"/> from MVC's
/// controller feature when running in Production.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Controllers.DevLoginController"/> depends on <see cref="DevPersonaSeeder"/>,
/// which is itself only registered outside Production (see <c>Program.cs</c>). With
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
/// that runs after the default <see cref="ControllerFeatureProvider"/> has populated the
/// controller list, and removes the dev controller from that list. Wired up via
/// <c>ConfigureApplicationPartManager</c> on the <c>IMvcBuilder</c> returned by
/// <c>AddControllersWithViews</c>, only when <c>IsProduction()</c> is true.
/// </para>
/// </remarks>
internal sealed class DevLoginControllerExclusionProvider : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        for (var i = feature.Controllers.Count - 1; i >= 0; i--)
        {
            if (feature.Controllers[i].AsType() == typeof(Controllers.DevLoginController))
                feature.Controllers.RemoveAt(i);
        }
    }
}
