using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using ShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;

namespace Humans.Application.Tests.Architecture;

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

    [HumansFact]
    public void ShiftManagementService_InjectsOnlyItsOwnRepository()
    {
        // The Shift Summary feature (BuildSummaryAsync) reaches Camps, Teams, and
        // Users only through their cross-section read interfaces
        // (ICampServiceRead / ITeamServiceRead / IUserServiceRead), resolved lazily
        // via IServiceProvider — never a foreign section's repository, and adding
        // no new cross-section interface. Pin that the only repository the service
        // takes is its own IShiftManagementRepository.
        var injectedRepositories = typeof(ShiftManagementService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t.IsInterface && typeof(IRepository).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        injectedRepositories.Should().BeEquivalentTo([nameof(IShiftManagementRepository)],
            because: "Summary must read other sections via I<Section>ServiceRead, never their repositories (peters-hard-rules: services call other sections only through read interfaces)");
    }
}
