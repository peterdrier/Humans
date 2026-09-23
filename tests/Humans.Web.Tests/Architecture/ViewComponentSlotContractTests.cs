using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Every view component <see cref="Type"/> a section contributes through a seam — the
/// <c>Component</c> in <see cref="ChromeComponent"/>, <c>SettingsTab</c>, <c>UserPart</c>, and
/// any future <see cref="ViewComponentSlotAttribute"/>-tagged seam — actually resolves to a
/// view component whose parameters the seam's declared args record can supply
/// (nobodies-collective/Humans#1815). A slot with no <c>[ViewComponentSlot]</c> anywhere it
/// could resolve one is exactly the drift #1810 caught by hand: a component that quietly
/// drops an argument the host never checked.
/// </summary>
/// <remarks>
/// <para>
/// Discovery walks every <see cref="ISectionContribution"/> the app would register
/// (<see cref="SectionDiscoveryExtensions.DiscoverImplementations{T}"/>, the same walk
/// production DI uses) and every seam interface it implements — never a hand-maintained
/// list, so a new contributor or a new <c>[ViewComponentSlot]</c> seam is covered
/// automatically. For each seam it reads every <c>Type</c>-valued property, and every
/// <c>Type</c>-valued property of the elements of every parameterless
/// <c>IEnumerable&lt;T&gt;</c> method — the two shapes a seam declares a component through
/// today.
/// </para>
/// <para>
/// The args contract check (<see cref="CheckComponentContract"/>) is a pure function of a
/// component <see cref="Type"/>, a resolved <see cref="ViewComponentSlotAttribute"/>, and
/// whether the site is a single-contributor property or a fanout element, so it is exercised
/// against fake components directly — a missing non-optional argument, a type mismatch on an
/// <em>optional</em> parameter (the #1810 shape), and a single-contributor args property that
/// binds to no parameter (a dropped argument, tolerated only on a fanout) — as well as over
/// the real app roster.
/// </para>
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
        // (b): the parameter is optional, so presence alone would pass — but a same-named
        // property of the wrong type is exactly the #1810 drift (renamed/retyped argument).
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
        // (c): on a single-contributor (property) seam, every args-record property must bind
        // to some Invoke parameter — a property the component never reads is a dropped argument.
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
        // Same shape as the previous test, but on a multi-contributor fanout: MVC ignores
        // args properties a given contributor's Invoke doesn't declare, so this must not fail.
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

    /// <param name="IsSingleContributor">True for a <c>Type</c> property declared directly on the
    /// seam interface (one contributor per slot, e.g. <c>IOnboardingShiftsStep.ShiftsList</c>) —
    /// where a dropped args property is drift. False for an element of a fanout's
    /// <c>IEnumerable&lt;T&gt;</c> (many contributors share the args shape; MVC ignores properties
    /// a given contributor's <c>Invoke</c> doesn't declare).</param>
    private sealed record SlotSite(Type Seam, PropertyInfo? OwnProperty, Type Component, string Location, bool IsSingleContributor);

    /// <summary>The seam interfaces a contribution type implements — mirrors the private
    /// method of the same shape in <see cref="SectionDiscoveryExtensions"/>.</summary>
    private static IEnumerable<Type> SeamInterfaces(Type contributionType) =>
        contributionType.GetInterfaces()
            .Where(i => i != typeof(ISectionContribution) && typeof(ISectionContribution).IsAssignableFrom(i));

    /// <summary>
    /// Every <c>Type</c>-valued slot site a seam interface declares: a direct <c>Type</c>
    /// property, or a <c>Type</c> property of the elements a parameterless
    /// <c>IEnumerable&lt;T&gt;</c> method returns.
    /// </summary>
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

    /// <summary>Property first, then the seam interface — a future per-slot override always wins.</summary>
    private static ViewComponentSlotAttribute? ResolveSlotAttribute(SlotSite site) =>
        site.OwnProperty?.GetCustomAttribute<ViewComponentSlotAttribute>()
            ?? site.Seam.GetCustomAttribute<ViewComponentSlotAttribute>();

    /// <summary>
    /// Whether <paramref name="component"/>'s declared parameters are all satisfiable by the
    /// slot's args record: a pure function of a component type and its resolved attribute, so
    /// it can be exercised directly against a fake component as well as the real app roster.
    /// </summary>
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

            // Checked whether or not the parameter is optional: a same-named property of the
            // wrong type is the #1810 drift (renamed/retyped argument), and MVC's name-based
            // binding does not care that the parameter has a default.
            if (!IsAssignable(parameter.ParameterType, match.PropertyType))
            {
                yield return $"{location}: {component.FullName}.{invokeMethods[0].Name} parameter "
                    + $"'{parameter.Name}' ({parameter.ParameterType}) is not assignable from "
                    + $"{attribute.Args!.FullName}.{match.Name} ({match.PropertyType})";
            }
        }

        // On a single-contributor (property) seam, an args property that binds to no parameter
        // is a dropped argument — the component silently stopped reading it. Fanouts share one
        // args shape across contributors, so an unbound property there is normal, not drift.
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

    /// <summary><paramref name="propertyType"/> can supply <paramref name="parameterType"/> — including a
    /// non-nullable value type (<c>Guid</c>) supplying a nullable parameter (<c>Guid?</c>), which
    /// <see cref="Type.IsAssignableFrom"/> alone does not recognize.</summary>
    private static bool IsAssignable(Type parameterType, Type propertyType)
    {
        if (parameterType.IsAssignableFrom(propertyType)) return true;

        var underlying = Nullable.GetUnderlyingType(parameterType);
        return underlying is not null && underlying.IsAssignableFrom(propertyType);
    }

    /// <summary>Mirrors <see cref="Humans.Web.Hosting.SectionViewComponentFeatureProvider"/>'s own
    /// component test (also duplicated by <c>ViewComponentTagHelperBindingTests</c>).</summary>
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
