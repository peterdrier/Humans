using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Cantina.Services;
using Humans.Users.Contracts;

namespace Humans.Cantina.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Cantina
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class CantinaArchitectureTests
{
    [HumansFact]
    public void SectionAssemblyDoesNotReferenceEntityFrameworkCore()
    {
        // Cantina owns no tables — it reads everything through other sections' services.
        // Without an EF reference it can't even name a DbContext. Checking the reference
        // catches the section gaining one; a constructor check would not.
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
