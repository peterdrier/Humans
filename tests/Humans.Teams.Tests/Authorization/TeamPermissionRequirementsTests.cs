using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.Teams.Authorization;
using Humans.Teams.Contracts;
using Xunit;

namespace Humans.Teams.Tests.Authorization;

public sealed class TeamPermissionRequirementsTests
{
    [HumansFact]
    public void Manage_EarlyEntryAdmin_HoldsOnlyEarlyEntry()
    {
        TeamPermissionRequirements.ManagePermission.AdminRoles.Should().Equal(TeamsRoles.TeamsAdmin);
        TeamPermissionRequirements.EarlyEntryPermission.AdminRoles
            .Should().Equal(TeamsRoles.TeamsAdmin, EarlyEntryRoles.EarlyEntryAdmin);
    }
}
