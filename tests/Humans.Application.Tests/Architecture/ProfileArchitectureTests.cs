using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Xunit;
using ProfileService = Humans.Application.Services.Profiles.ProfileService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/store/decorator pattern for the
/// Profile section.
/// </summary>
public class ProfileArchitectureTests
{
    [HumansFact]
    public void ProfileService_has_no_outbound_edge_to_teams_or_stores()
    {
        var ctor = typeof(ProfileService).GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToList();

        paramTypes.Should().NotContain(typeof(ITeamService),
            because: "Profile is foundational; team deletion cascade is owned elsewhere");
        parameters.Should().NotContain(
            p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal),
            because: "Profile services must not depend on store abstractions");
    }

    [HumansFact]
    public void Profile_lookup_by_user_ids_does_not_exist_on_profile_surface()
    {
        // T-05 cache migration: GetByUserIdsAsync was the last fan-out path that
        // re-loaded Profile rows from the DB after a caller already had the
        // user ids in hand. Every consumer now reads UserInfo.Profile from the
        // unified read-model (UserInfo) instead. Re-adding GetByUserIdsAsync to
        // either surface would resurrect the parallel-read-path divergence the
        // unified read-model was built to eliminate — pin it.
        typeof(IProfilePictureService)
            .GetMethod("GetByUserIdsAsync")
            .Should().BeNull(
                because: "picture service callers read UserInfo.Profile via IUserService.GetUserInfoAsync / GetUserInfosAsync");

        typeof(IProfileRepository)
            .GetMethod("GetByUserIdsAsync")
            .Should().BeNull(
                because: "IProfileRepository has no fan-out reader path; the only legitimate batched profile read is UserInfo.Profile off the cache");
    }
}
