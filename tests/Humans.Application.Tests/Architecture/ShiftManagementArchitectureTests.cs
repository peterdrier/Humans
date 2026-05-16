using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Infrastructure.Repositories.Shifts;
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
    [HumansFact]
    public void ShiftManagementService_TakesRepository()
    {
        var ctor = typeof(ShiftManagementService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftManagementRepository));
    }

    [HumansFact]
    public void ShiftManagementService_ImplementsShiftAuthorizationInvalidator()
    {
        typeof(IShiftAuthorizationInvalidator).IsAssignableFrom(typeof(ShiftManagementService))
            .Should().BeTrue(
                because: "the service owns the shift-auth cache and external sections (Profile deletion) drop it through this invalidator rather than poking IMemoryCache directly");
    }

    [HumansFact]
    public void ShiftManagementRepository_IsSealed()
    {
        var repoType = typeof(ShiftManagementRepository);
        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [HumansFact]
    public void ShiftsOwnedEntities_HaveNoCrossDomainNavigationProperties()
    {
        // Cross-domain navs (Rota.Team, ShiftSignup.User/EnrolledByUser/ReviewedByUser,
        // VolunteerEventProfile.User, VolunteerTagPreference.User) were removed in
        // §15 Part 1 (issue #541). Display fields are resolved via ITeamService /
        // IUserService at the Application + Web layers. Cross-domain FKs stay
        // wired in EF via the typed-FK form (HasOne<T>().WithMany().HasForeignKey(...)).
        var crossDomainNavTypes = new[] { typeof(User), typeof(Team) };
        var shiftsOwnedEntities = new[]
        {
            typeof(Rota),
            typeof(ShiftSignup),
            typeof(VolunteerEventProfile),
            typeof(VolunteerTagPreference)
        };

        foreach (var entity in shiftsOwnedEntities)
        {
            var crossDomainNavs = entity.GetProperties()
                .Where(p => crossDomainNavTypes.Contains(p.PropertyType))
                .Select(p => p.Name)
                .ToList();

            crossDomainNavs.Should().BeEmpty(
                because: $"{entity.Name} must not expose User/Team navigation properties — resolve through ITeamService / IUserService instead (design-rules §6c)");
        }
    }
}
