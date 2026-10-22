using System.Reflection;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Hosting;

/// <summary>
/// Lets a section project's controllers be <c>internal</c>. MVC's default <see cref="ControllerFeatureProvider"/>
/// requires <c>IsPublic</c>, and an internal controller is then silently not discovered: the build is green and the
/// route 404s.
/// </summary>
/// <remarks>
/// A section's rule is "public means <c>Section</c> or <c>Contracts/</c>",
/// with no exceptions — that is what makes the compiler the boundary. Without this the
/// rule would have to carve out controllers in all ~35 sections, and a section's
/// controllers, its action-parameter view models and everything those touch would be
/// nameable from any other section. Scoped to assemblies declaring an
/// <c>ISection</c> entry point, the same test the analyzers and DI discovery use.
/// </remarks>
internal sealed class SectionControllerFeatureProvider : ControllerFeatureProvider
{
    private const string ControllerSuffix = "Controller";

    protected override bool IsController(TypeInfo typeInfo)
    {
        // A deactivated section routes nothing, public or not: the base provider would otherwise still take its
        // public controllers.
        if (SectionDiscoveryExtensions.IsInactiveSection(typeInfo.Assembly))
            return false;

        if (base.IsController(typeInfo))
            return true;

        // Only relax the public check, and only for section assemblies. Every other
        // condition below mirrors ControllerFeatureProvider.IsController.
        if (!SectionDiscoveryExtensions.IsActiveSection(typeInfo.Assembly))
            return false;

        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.ContainsGenericParameters)
            return false;

        if (typeInfo.IsDefined(typeof(NonControllerAttribute)))
            return false;

        return typeInfo.Name.EndsWith(ControllerSuffix, StringComparison.OrdinalIgnoreCase)
            || typeInfo.IsDefined(typeof(ControllerAttribute));
    }
}
