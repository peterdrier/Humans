using AwesomeAssertions;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the AccountDeletionService orchestrator.
/// </summary>
public class AccountDeletionArchitectureTests
{
    [HumansFact]
    public void IAccountDeletionService_LivesInApplicationInterfacesUsersNamespace()
    {
        typeof(IAccountDeletionService).Namespace
            .Should().Be("Humans.Application.Interfaces.Users",
                because: "IAccountDeletionService lives alongside IUserService; it is the orchestration surface for the User-section deletion lifecycle");
    }
}
