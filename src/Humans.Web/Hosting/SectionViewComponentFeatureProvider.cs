using System.Reflection;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ViewComponents;

namespace Humans.Web.Hosting;

/// <summary>
/// Lets a section project's view components be <c>internal</c>, for the same reason as
/// <see cref="SectionControllerFeatureProvider"/>: an internal component is never discovered, and invoking it by
/// <c>Type</c> throws at request time.
/// </summary>
/// <remarks>
/// <c>ViewComponentFeatureProvider.PopulateFeature</c> is not virtual and <c>ViewComponentConventions</c> is internal
/// to MVC, so this runs as a second pass adding only non-public types from section assemblies. Every other condition
/// mirrors <c>ViewComponentConventions.IsComponent</c>.
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

        // The default provider already took every public one, including from deactivated sections; drop those, or
        // they resolve services nobody registered.
        for (var i = feature.ViewComponents.Count - 1; i >= 0; i--)
        {
            if (SectionDiscoveryExtensions.IsInactiveSection(feature.ViewComponents[i].Assembly))
                feature.ViewComponents.RemoveAt(i);
        }
    }

    private static bool IsSectionViewComponent(TypeInfo typeInfo)
    {
        // Only relax the public check, and only for section assemblies. The default
        // provider has already taken every public one.
        if (typeInfo.IsPublic)
            return false;

        if (!SectionDiscoveryExtensions.IsActiveSection(typeInfo.Assembly))
            return false;

        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.ContainsGenericParameters)
            return false;

        if (typeInfo.IsDefined(typeof(NonViewComponentAttribute)))
            return false;

        return typeInfo.Name.EndsWith(ViewComponentSuffix, StringComparison.Ordinal)
            || typeInfo.IsDefined(typeof(ViewComponentAttribute));
    }
}
