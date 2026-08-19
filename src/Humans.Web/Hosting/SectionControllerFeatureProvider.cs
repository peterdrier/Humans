using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Hosting;

/// <summary>
/// Lets a section project's controllers be <c>internal</c>
/// (nobodies-collective/Humans#866). MVC's default
/// <see cref="ControllerFeatureProvider"/> requires <c>IsPublic</c>, so an internal
/// controller is not discovered — and nothing says so: the build is green with zero
/// warnings and the route simply 404s. The same silent-failure class as a missing
/// section <c>_ViewImports.cshtml</c>, and measured the same way (design §1).
/// </summary>
/// <remarks>
/// A section's rule is "public means <c>Section</c> or <c>Contracts/</c>",
/// with no exceptions — that is what makes the compiler the boundary. Without this the
/// rule would have to carve out controllers in all ~35 sections, and a section's
/// controllers, its action-parameter view models and everything those touch would be
/// nameable from any other section. Scoped to assemblies declaring an
/// <c>ISection</c> entry point, the same test the analyzers and DI discovery use.
/// </remarks>
internal sealed class SectionControllerFeatureProvider(SectionAssemblySnapshot sections)
    : ControllerFeatureProvider
{
    private const string ControllerSuffix = "Controller";

    protected override bool IsController(TypeInfo typeInfo)
    {
        // A deactivated section routes nothing, public or not — the base provider would
        // otherwise still take its public controllers (#1081).
        if (sections.IsInactiveSection(typeInfo.Assembly))
            return false;

        if (base.IsController(typeInfo))
            return true;

        // Only relax the public check, and only for section assemblies. Every other
        // condition below mirrors ControllerFeatureProvider.IsController.
        if (!sections.IsActiveSection(typeInfo.Assembly))
            return false;

        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.ContainsGenericParameters)
            return false;

        if (typeInfo.IsDefined(typeof(NonControllerAttribute)))
            return false;

        return typeInfo.Name.EndsWith(ControllerSuffix, StringComparison.OrdinalIgnoreCase)
            || typeInfo.IsDefined(typeof(ControllerAttribute));
    }
}
