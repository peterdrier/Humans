using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ViewComponents;

namespace Humans.Web.Hosting;

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
internal sealed class SectionViewComponentFeatureProvider(SectionAssemblySnapshot sections)
    : IApplicationFeatureProvider<ViewComponentFeature>
{
    private const string ViewComponentSuffix = "ViewComponent";

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ViewComponentFeature feature)
    {
        foreach (var part in parts.OfType<IApplicationPartTypeProvider>())
        {
            foreach (var type in part.Types)
            {
                if (IsSectionViewComponent(type, sections) && !feature.ViewComponents.Contains(type))
                    feature.ViewComponents.Add(type);
            }
        }

        // The default provider already took every public one, including from sections this
        // deployment deactivated; drop those, or they resolve services nobody registered (#1081).
        for (var i = feature.ViewComponents.Count - 1; i >= 0; i--)
        {
            if (sections.IsInactiveSection(feature.ViewComponents[i].Assembly))
                feature.ViewComponents.RemoveAt(i);
        }
    }

    private static bool IsSectionViewComponent(TypeInfo typeInfo, SectionAssemblySnapshot sections)
    {
        // Only relax the public check, and only for section assemblies. The default
        // provider has already taken every public one.
        if (typeInfo.IsPublic)
            return false;

        if (!sections.IsActiveSection(typeInfo.Assembly))
            return false;

        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.ContainsGenericParameters)
            return false;

        if (typeInfo.IsDefined(typeof(NonViewComponentAttribute)))
            return false;

        return typeInfo.Name.EndsWith(ViewComponentSuffix, StringComparison.Ordinal)
            || typeInfo.IsDefined(typeof(ViewComponentAttribute));
    }
}
