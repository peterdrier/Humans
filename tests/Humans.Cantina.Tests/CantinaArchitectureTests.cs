using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Cantina.Contracts;
using Humans.Cantina.Services;
using Humans.Users.Contracts;

namespace Humans.Cantina.Tests;

public class CantinaArchitectureTests
{
    [HumansFact]
    public void RosterServiceReadsOtherSectionsThroughPublicContracts()
    {
        // The invariants doc's load-bearing claim: the cantina never touches the Shifts
        // repository; names come off the cached UserInfo and dietary through Cantina's own service.
        var paramTypes = typeof(CantinaRosterService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(ICantinaService));
    }

    [HumansFact]
    public void RosterDtosCarryNoMedicalConditions()
    {
        // GDPR Article 9 boundary, and the reason this section exists in the shape it does:
        // the cantina plans around food, not medical history. MedicalConditions rides on the
        // cached UserInfo (its Profile) the service already holds for names, so nothing but this stops it
        // being projected out (docs Cantina.md — Negative Access Rules).
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
