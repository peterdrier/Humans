using AwesomeAssertions;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Pins the account-merge and duplicate-detection surfaces to their Users-section
/// home (moved from Profiles in the merge consolidation). Account merge tombstones
/// User rows and duplicate detection scans User emails — both are User-section concerns.
/// </summary>
public class AccountMergeArchitectureTests
{
    [HumansFact]
    public void IAccountMergeService_LivesInApplicationInterfacesUsersNamespace()
    {
        typeof(IAccountMergeService).Namespace
            .Should().Be("Humans.Application.Interfaces.Users",
                because: "account merge is a User-section concern (it tombstones User rows); it lives alongside IUserService");
    }

    [HumansFact]
    public void IDuplicateAccountService_LivesInApplicationInterfacesUsersNamespace()
    {
        typeof(IDuplicateAccountService).Namespace
            .Should().Be("Humans.Application.Interfaces.Users",
                because: "duplicate detection scans User emails; it lives alongside IUserService");
    }
}
