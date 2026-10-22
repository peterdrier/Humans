using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Every view component a section contributes through a seam resolves to a component whose parameters the seam's
/// args record can supply, so a component can't silently drop an argument the host passes.
/// </summary>
/// <remarks>
/// Discovery uses the same walk as production DI, so new contributors and seams are covered automatically. A seam
/// declares a component as a <c>Type</c> property, or a <c>Type</c> property on the elements of a parameterless
/// <c>IEnumerable&lt;T&gt;</c> method.
/// </remarks>
public class ViewComponentSlotContractTests
{
    private const string ViewComponentSuffix = "ViewComponent";

    [HumansFact]
    public void EveryContributedComponentType_SatisfiesItsSlotContract()
    {
        var contributions = SectionDiscoveryExtensions.DiscoverImplementations<ISectionContribution>();

        contributions.Should().NotBeEmpty(
            "the scan must find the contributions it is guarding — an empty set is a broken sweep");

        var failures = new List<string>();
        var sitesChecked = 0;

        foreach (var contribution in contributions)
        {
            foreach (var seam in SeamInterfaces(contribution.GetType()))
            {
                foreach (var site in Sites(contribution, seam))
                {
                    sitesChecked++;
                    var attribute = ResolveSlotAttribute(site);

                    if (attribute is null)
                    {
                        failures.Add($"{site.Location}: no [ViewComponentSlot] resolves for this member "
                            + "(checked the property, then the seam interface)");
                        continue;
                    }

                    failures.AddRange(CheckComponentContract(site.Component, attribute, site.Location, site.IsSingleContributor));
                }
            }
        }

        sitesChecked.Should().BeGreaterThan(0,
            "the scan must find Type-valued slot sites to check — an empty sweep is a broken sweep, "
            + "not a clean bill of health");

        failures.Should().BeEmpty(because: string.Join(Environment.NewLine, failures));
    }

    [HumansFact]
    public void ACheckedComponent_MissingANonOptionalArgument_Fails()
    {
        var failures = CheckComponentContract(
            typeof(FakeMissingArgumentViewComponent),
            new ViewComponentSlotAttribute(typeof(FakeArgs)),
            "unit test",
            isSingleContributor: false).ToList();

        failures.Should().ContainSingle(because: "requiredButUnsupplied has no matching property on FakeArgs");
    }

    [HumansFact]
    public void ANoArgsSlot_WithAllOptionalParameters_Passes()
    {
        var failures = CheckComponentContract(
            typeof(FakeNoArgsViewComponent),
            new ViewComponentSlotAttribute(),
            "unit test",
            isSingleContributor: false).ToList();

        failures.Should().BeEmpty();
    }

    [HumansFact]
    public void ACheckedComponent_OptionalParameterWithATypeMismatchedProperty_Fails()
    {
        // The parameter is optional, so presence alone would pass; a same-named property of the wrong type is drift.
        var failures = CheckComponentContract(
            typeof(FakeOptionalTypeMismatchViewComponent),
            new ViewComponentSlotAttribute(typeof(FakeStringUserIdArgs)),
            "unit test",
            isSingleContributor: false).ToList();

        failures.Should().ContainSingle(because: "userId is optional but FakeStringUserIdArgs.UserId is a string, not a Guid");
    }

    [HumansFact]
    public void ASingleContributorSlot_WithAnArgsPropertyThatBindsToNoParameter_Fails()
    {
        var failures = CheckComponentContract(
            typeof(FakeSingleParameterViewComponent),
            new ViewComponentSlotAttribute(typeof(FakeArgsWithExtraProperty)),
            "unit test",
            isSingleContributor: true).ToList();

        failures.Should().ContainSingle(because: "FakeArgsWithExtraProperty.Extra binds to no parameter of the single contributor");
    }

    [HumansFact]
    public void AFanoutSlot_WithAnArgsPropertyThatBindsToNoParameter_Passes()
    {
        // MVC ignores args properties a fanout contributor's Invoke doesn't declare.
        var failures = CheckComponentContract(
            typeof(FakeSingleParameterViewComponent),
            new ViewComponentSlotAttribute(typeof(FakeArgsWithExtraProperty)),
            "unit test",
            isSingleContributor: false).ToList();

        failures.Should().BeEmpty();
    }

    private sealed record FakeArgs(Guid UserId);

    private sealed record FakeStringUserIdArgs(string UserId);

    private sealed record FakeArgsWithExtraProperty(Guid UserId, string Extra);

    private sealed class FakeMissingArgumentViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke(Guid userId, string requiredButUnsupplied) => Content(string.Empty);
    }

    private sealed class FakeNoArgsViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke(Guid? optional = null) => Content(string.Empty);
    }

    private sealed class FakeOptionalTypeMismatchViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke(Guid? userId = null) => Content(string.Empty);
    }

    private sealed class FakeSingleParameterViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke(Guid userId) => Content(string.Empty);
    }

    /// <summary>
    /// <paramref name="IsSingleContributor"/>: a <c>Type</c> property declared on the seam itself, where an unbound args
    /// property is drift; false for a fanout element, where contributors share one args shape.
    /// </summary>
    private sealed record SlotSite(Type Seam, PropertyInfo? OwnProperty, Type Component, string Location, bool IsSingleContributor);

    /// <summary>Mirrors the private method of the same shape in <see cref="SectionDiscoveryExtensions"/>.</summary>
    private static IEnumerable<Type> SeamInterfaces(Type contributionType) =>
        contributionType.GetInterfaces()
            .Where(i => i != typeof(ISectionContribution) && typeof(ISectionContribution).IsAssignableFrom(i));

    private static IEnumerable<SlotSite> Sites(object contribution, Type seam)
    {
        foreach (var property in seam.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(Type)) continue;

            if (property.GetValue(contribution) is Type component)
                yield return new SlotSite(seam, property, component, $"{contribution.GetType().FullName}.{seam.Name}.{property.Name}", IsSingleContributor: true);
        }

        foreach (var method in seam.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetParameters().Length != 0) continue;
            if (!method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            var elementType = method.ReturnType.GetGenericArguments()[0];
            var elementTypeProperties = elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(Type))
                .ToList();

            if (elementTypeProperties.Count == 0) continue;

            if (method.Invoke(contribution, null) is not System.Collections.IEnumerable elements) continue;

            foreach (var element in elements)
            {
                foreach (var elementProperty in elementTypeProperties)
                {
                    if (elementProperty.GetValue(element) is not Type component) continue;

                    yield return new SlotSite(
                        seam,
                        elementProperty,
                        component,
                        $"{contribution.GetType().FullName}.{seam.Name}.{method.Name}()[{elementType.Name}].{elementProperty.Name}",
                        IsSingleContributor: false);
                }
            }
        }
    }

    /// <summary>Property first, then the seam interface, so a per-slot override wins.</summary>
    private static ViewComponentSlotAttribute? ResolveSlotAttribute(SlotSite site) =>
        site.OwnProperty?.GetCustomAttribute<ViewComponentSlotAttribute>()
            ?? site.Seam.GetCustomAttribute<ViewComponentSlotAttribute>();

    private static IEnumerable<string> CheckComponentContract(
        Type component, ViewComponentSlotAttribute attribute, string location, bool isSingleContributor)
    {
        if (!IsViewComponentType(component))
        {
            yield return $"{location}: {component.FullName} does not look like a view component "
                + "(name does not end with \"ViewComponent\" and has no [ViewComponent] attribute)";
            yield break;
        }

        var invokeMethods = component.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "Invoke" or "InvokeAsync")
            .ToList();

        if (invokeMethods.Count != 1)
        {
            yield return $"{location}: {component.FullName} has {invokeMethods.Count} public "
                + "Invoke/InvokeAsync methods; a view component must have exactly one";
            yield break;
        }

        var argsProperties = attribute.Args?.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            ?? [];

        var parameters = invokeMethods[0].GetParameters();

        foreach (var parameter in parameters)
        {
            var match = argsProperties.FirstOrDefault(p =>
                string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                if (parameter.IsOptional) continue;

                yield return $"{location}: {component.FullName}.{invokeMethods[0].Name} requires non-optional "
                    + $"parameter '{parameter.Name}', which no property on "
                    + $"{(attribute.Args?.FullName ?? "(no args record — [ViewComponentSlot] declared no arguments)")} supplies";
                continue;
            }

            // Checked even for optional parameters: MVC binds by name whatever the default.
            if (!IsAssignable(parameter.ParameterType, match.PropertyType))
            {
                yield return $"{location}: {component.FullName}.{invokeMethods[0].Name} parameter "
                    + $"'{parameter.Name}' ({parameter.ParameterType}) is not assignable from "
                    + $"{attribute.Args!.FullName}.{match.Name} ({match.PropertyType})";
            }
        }

        // Fanouts share one args shape across contributors, so an unbound property is only drift on a single slot.
        if (isSingleContributor)
        {
            foreach (var argsProperty in argsProperties)
            {
                var bound = parameters.Any(p =>
                    string.Equals(p.Name, argsProperty.Name, StringComparison.OrdinalIgnoreCase));

                if (!bound)
                {
                    yield return $"{location}: {attribute.Args!.FullName}.{argsProperty.Name} binds to no "
                        + $"parameter of {component.FullName}.{invokeMethods[0].Name} (dropped argument)";
                }
            }
        }
    }

    /// <summary>Also accepts <c>Guid</c> supplying <c>Guid?</c>, which <see cref="Type.IsAssignableFrom"/> rejects.</summary>
    private static bool IsAssignable(Type parameterType, Type propertyType)
    {
        if (parameterType.IsAssignableFrom(propertyType)) return true;

        var underlying = Nullable.GetUnderlyingType(parameterType);
        return underlying is not null && underlying.IsAssignableFrom(propertyType);
    }

    /// <summary>Mirrors <see cref="Humans.Web.Hosting.SectionViewComponentFeatureProvider"/>'s own component test.</summary>
    private static bool IsViewComponentType(Type type)
    {
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            return false;

        if (type.IsDefined(typeof(NonViewComponentAttribute)))
            return false;

        return type.Name.EndsWith(ViewComponentSuffix, StringComparison.Ordinal)
            || type.IsDefined(typeof(ViewComponentAttribute));
    }
}
