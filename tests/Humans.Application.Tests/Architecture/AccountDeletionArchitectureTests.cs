using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using AccountDeletionService = Humans.Application.Services.Users.AccountLifecycle.AccountDeletionService;

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

    [HumansFact]
    public void AccountDeletionService_HasNoRepositoryConstructorParameter()
    {
        // Orchestrator-no-repository guard. No universal enforcer covers this yet
        // (A2 orchestrator-no-repository analyzer is deferred); HUM0017 only catches
        // cross-section repository injection, not a same-section repo injected into
        // an orchestrator. Kept until A2 lands (matches Onboarding/HumanLifecycle).
        var ctor = typeof(AccountDeletionService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal),
                because: "the orchestrator owns no tables — cross-section reads/writes route through service interfaces, not repositories (design-rules §2c, §9)");
    }
}
