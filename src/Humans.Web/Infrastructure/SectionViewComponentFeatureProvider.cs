using System.Reflection;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ViewComponents;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Lets a section project's view components be <c>internal</c>
/// (nobodies-collective/Humans#866) — the exact counterpart of
/// <see cref="SectionControllerFeatureProvider"/>, and needed for the same reason:
/// MVC's <c>ViewComponentConventions.IsComponent</c> requires <c>IsPublic</c>, so an
/// internal view component is never discovered and
/// <c>Component.InvokeAsync("Name")</c> throws at request time.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the controller case there is no base provider to subclass —
/// <c>ViewComponentFeatureProvider.PopulateFeature</c> is not virtual and
/// <c>ViewComponentConventions</c> is internal to MVC — so this runs as a second pass
/// after the default provider and adds only what that one skipped: non-public types in
/// assemblies declaring an <c>ISection</c> entry point. Every other condition mirrors
/// <c>ViewComponentConventions.IsComponent</c>.
/// </para>
/// <para>
/// First needed by Notifications, whose bell is chrome rendered from Shell's
/// <c>_Layout</c> and <c>_AdminLayout</c>. Leaving the component in
/// Shell would have split the section's 38-key resource set, since its markup renders
/// two of them (design §15 step 3b).
/// </para>
/// </remarks>
internal sealed class SectionViewComponentFeatureProvider
    : IApplicationFeatureProvider<ViewComponentFeature>
{
    private const string ViewComponentSuffix = "ViewComponent";

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ViewComponentFeature feature)
    {
        foreach (var part in parts.OfType<IApplicationPartTypeProvider>())
        {
            foreach (var type in part.Types)
            {
                if (IsSectionViewComponent(type) && !feature.ViewComponents.Contains(type))
                    feature.ViewComponents.Add(type);
            }
        }
    }

    private static bool IsSectionViewComponent(TypeInfo typeInfo)
    {
        // Only relax the public check, and only for section assemblies. The default
        // provider has already taken every public one.
        if (typeInfo.IsPublic)
            return false;

        if (!SectionDiscoveryExtensions.IsSectionAssembly(typeInfo.Assembly))
            return false;

        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.ContainsGenericParameters)
            return false;

        if (typeInfo.IsDefined(typeof(NonViewComponentAttribute)))
            return false;

        return typeInfo.Name.EndsWith(ViewComponentSuffix, StringComparison.Ordinal)
            || typeInfo.IsDefined(typeof(ViewComponentAttribute));
    }
}
