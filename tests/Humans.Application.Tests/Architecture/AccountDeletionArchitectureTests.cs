using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AccountDeletionService = Humans.Application.Services.Users.AccountLifecycle.AccountDeletionService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for <see cref="AccountDeletionService"/> — the single
/// orchestrator for user-requested, admin-initiated, and expiry-triggered
/// account deletion (issue #582). Enforces the orchestrator shape so future
/// drift back into per-section cascade code fails loudly.
/// </summary>
public class AccountDeletionArchitectureTests
{
    [HumansFact]
    public void AccountDeletionService_LivesInApplicationUsersAccountLifecycleNamespace()
    {
        typeof(AccountDeletionService).Namespace
            .Should().Be("Humans.Application.Services.Users.AccountLifecycle",
                because: "AccountDeletionService is part of the User section's deletion-orchestration subfolder (issue #582)");
    }

    [HumansFact]
    public void AccountDeletionService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(AccountDeletionService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "the orchestrator owns no tables and must never inject DbContext (design-rules §3)");
    }

    [HumansFact]
    public void AccountDeletionService_HasNoIDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(AccountDeletionService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.EntityFrameworkCore.IDbContextFactory", StringComparison.Ordinal));

        factoryParam.Should().BeNull(
            because: "the orchestrator owns no tables — IDbContextFactory has no legitimate use (design-rules §9)");
    }

    [HumansFact]
    public void AccountDeletionService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(AccountDeletionService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "the orchestrator owns no cached data; invalidation is driven through the owning-section invalidator interfaces");
    }

    [HumansFact]
    public void IAccountDeletionService_LivesInApplicationInterfacesUsersNamespace()
    {
        typeof(IAccountDeletionService).Namespace
            .Should().Be("Humans.Application.Interfaces.Users",
                because: "IAccountDeletionService lives alongside IUserService; it is the orchestration surface for the User-section deletion lifecycle");
    }
}
