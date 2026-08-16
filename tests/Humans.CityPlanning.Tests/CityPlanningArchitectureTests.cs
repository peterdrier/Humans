using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Microsoft.Extensions.Localization;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for City Planning
/// (nobodies-collective/Humans#866, G5).
///
/// <para>
/// City Planning chose <b>Option A</b> (no caching decorator, no dict cache,
/// no DTO layer on top of the repository return types). It is a small,
/// admin-facing section with no hot bulk-read path — the same rationale used
/// by Users (#243) and Governance (#242) when they skipped the decorator.
/// </para>
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/CityPlanningArchitectureTests.cs</c>. Its
/// "the service takes no store type" assertion pinned the Application/Infrastructure split the
/// section no longer has; one assembly with one internal surface subsumes it. The append-only
/// repository assertion is kept below because it is about <c>camp_polygon_histories</c>, not
/// about where the section lives.
/// </remarks>
public class CityPlanningArchitectureTests
{





    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The carve moved every CityPlanning_* key out of SharedResource, so a type still
        // injecting IStringLocalizer<SharedResource> resolves nothing and renders the raw key —
        // a 200 with degraded copy, in every language. ContainersResource is allowed: the
        // barrio container pages are City Planning's URLs over Containers' vocabulary, and
        // Container_* / ContainerMap_* stay with their owner (§15 step 3b, carve by owner).
        // Views are safe by construction (_ViewImports rebinds all three localizers); this
        // catches a controller, which the render tests would not.
        var allowed = new[] { typeof(CityPlanningResource), typeof(Containers.ContainersResource) };

        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && !allowed.Contains(p.ParameterType.GetGenericArguments()[0]))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every CityPlanning_* key lives in CityPlanningResource; resolving one "
                   + "through another set renders the key itself and no error (§15 step 3b)");
    }

    /// <summary>
    /// Pins the set of types that may inject <see cref="ICityPlanningRepository"/>: the owning
    /// service and the repository implementation. A new consumer taking the repository directly
    /// would bypass the service layer and the single-writer rule for this section's tables.
    /// </summary>
    [HumansFact]
    public void ICityPlanningRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.CityPlanning.Services.CityPlanningService",
            "Humans.CityPlanning.Data.CityPlanningRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICityPlanningRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to this section's tables must go through the section's service");
    }

    [HumansFact]
    public void CityPlanningService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(CityPlanningService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the City Planning §15 migration went further and does not use a store at all");
    }

    [HumansFact]
    public void ICityPlanningRepository_HasNoHistoryUpdateOrDeleteMethods()
    {
        // CampPolygonHistory is append-only per design-rules §12.
        // The repository must not expose an UpdateAsync or DeleteAsync surface for it.
        var methods = typeof(ICityPlanningRepository).GetMethods().Select(m => m.Name).ToList();

        methods.Should().NotContain(
            [
                "UpdateHistoryAsync",
                "DeleteHistoryAsync",
                "RemoveHistoryAsync"
            ],
            because: "CampPolygonHistory is append-only (§12); repositories for append-only tables expose only Add/Get methods");
    }

    [HumansFact]
    public void CityPlanningEntities_HaveNoCrossSectionNavigationProperties()
    {
        // A polygon's camp season and its editing user are bare Guid references; the Camps and
        // Identity tables stay outside this model (memory/architecture/no-cross-section-ef-joins).
        var offenders = typeof(Section).Assembly.GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.CityPlanning.Domain", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties()
                .Where(p => (p.PropertyType.Namespace ?? string.Empty)
                    .StartsWith("Humans.Domain.Entities", StringComparison.Ordinal))
                .Select(p => $"{t.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty();
    }
}
