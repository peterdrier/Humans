using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the
/// <c>ShiftManagementService</c> portion of the Shifts section (issue #541a).
/// Sibling services (<c>ShiftSignupService</c>, <c>GeneralAvailabilityService</c>)
/// migrate in follow-up sub-tasks.
/// </summary>
public class ShiftManagementArchitectureTests
{
    [Fact]
    public void ShiftManagementService_LivesInHumansApplicationServicesShiftsNamespace()
    {
        typeof(ShiftManagementService).Namespace
            .Should().Be("Humans.Application.Services.Shifts",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void ShiftManagementService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ShiftManagementService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IShiftManagementRepository (design-rules §3)");
    }

    [Fact]
    public void ShiftManagementService_TakesRepository()
    {
        var ctor = typeof(ShiftManagementService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftManagementRepository));
    }

    [Fact]
    public void ShiftManagementService_ImplementsShiftAuthorizationInvalidator()
    {
        typeof(IShiftAuthorizationInvalidator).IsAssignableFrom(typeof(ShiftManagementService))
            .Should().BeTrue(
                because: "the service owns the shift-auth cache and external sections (Profile deletion) drop it through this invalidator rather than poking IMemoryCache directly");
    }

    [Fact]
    public void IShiftManagementRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IShiftManagementRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void ShiftManagementRepository_IsSealed()
    {
        var repoType = typeof(ShiftManagementRepository);
        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
