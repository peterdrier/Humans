using AwesomeAssertions;
using Humans.Shifts.Contracts;
using ShiftManagementService = Humans.Shifts.Services.ShiftManagementService;

namespace Humans.Shifts.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the
/// <c>ShiftManagementService</c> portion of the Shifts section (issue #541a).
/// Sibling services (<c>ShiftSignupService</c>, <c>VolunteerTrackingService</c>)
/// cover signup and user-oriented tracking workflows.
/// </summary>
public class ShiftManagementArchitectureTests
{
    [HumansFact]
    public void ShiftManagementService_ImplementsShiftAuthorizationInvalidator()
    {
        typeof(IShiftAuthorizationInvalidator).IsAssignableFrom(typeof(ShiftManagementService))
            .Should().BeTrue(
                because: "the service owns the shift-auth cache and external sections (Profile deletion) drop it through this invalidator rather than poking IMemoryCache directly");
    }
}
