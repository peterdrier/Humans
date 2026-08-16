using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Cantina.Services;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

namespace Humans.Cantina.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Cantina
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class CantinaArchitectureTests
{


    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // A view is safe by construction — Views/_ViewImports.cshtml rebinds Localizer in one
        // line — but a *controller* left on IStringLocalizer<SharedResource> keeps compiling
        // and renders its carved keys as raw key names on exactly the POST and failure paths a
        // render test does not reach. That is how Consent shipped five raw keys past a green
        // 5,370-test suite (§15 step 3b). Cantina carved all 44 of its keys and reads no shared
        // one, so the guard admits a single marker.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => p.ParameterType.IsGenericType
                            && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                            && p.ParameterType.GetGenericArguments()[0] != typeof(CantinaResource))
                .Select(_ => t.FullName ?? t.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every Cantina_* key moved into CantinaResource; binding any other set "
                   + "renders the raw key in all six languages");
    }

    [HumansFact]
    public void SectionAssemblyDoesNotReferenceEntityFrameworkCore()
    {
        // Calendar's rule (§15 step 11) retires this assertion shape for a moved section,
        // because a section assembly holds its own repository and references EF on purpose —
        // so the test either fails or, worse, keeps passing while asserting nothing. Cantina
        // is the case where the original statement is still true and is the strongest one
        // available: the section owns no tables, takes no Humans.Infrastructure reference and
        // reads everything through other sections' service interfaces, so it cannot *name* a
        // DbContext. Restating it on the constructors would need an EF package this project
        // deliberately does not have (Scanner's rule, step 8).
        typeof(Section).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should().NotContain("Microsoft.EntityFrameworkCore",
                because: "Cantina composes over other sections' services and owns no tables");
    }

    [HumansFact]
    public void RosterServiceReadsOtherSectionsThroughReadInterfaces()
    {
        // The invariants doc's load-bearing claim: the cantina never touches the Shifts
        // repository, and dietary comes off the cached UserInfo rather than an entity read.
        var paramTypes = typeof(CantinaRosterService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftManagementServiceRead));
        paramTypes.Should().Contain(typeof(IUserServiceRead));
        paramTypes.Should().NotContain(typeof(IUserService),
            because: "cross-section user reads must use the read interface "
                   + "(section-read-write-split / HUM0032)");
        paramTypes.Should().NotContain(
            t => t.Name.EndsWith("Repository", StringComparison.Ordinal),
            because: "only a section's own repository may be injected, and Cantina has none");
    }

    [HumansFact]
    public void RosterDtosCarryNoMedicalConditions()
    {
        // GDPR Article 9 boundary, and the reason this section exists in the shape it does:
        // the cantina plans around food, not medical history. MedicalConditions is on the
        // cached ProfileInfo the service already holds, so nothing but this stops it being
        // projected out (docs Cantina.md — Negative Access Rules).
        var offenders = typeof(Section).Assembly.GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.Cantina.Services.Dtos", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => name.Contains("Medical", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "MedicalConditions is never surfaced through the Cantina section, "
                   + "regardless of viewer role");
    }
}
