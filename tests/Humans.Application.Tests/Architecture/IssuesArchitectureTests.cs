using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Issues;
using Microsoft.EntityFrameworkCore;
using Xunit;
using IssuesService = Humans.Application.Services.Issues.IssuesService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Issues
/// section. Issues is a per-section queue triaged by handlers; no caching
/// decorator sits in front of the service — the service goes directly through
/// <see cref="IIssuesRepository"/> and invalidates the nav-badge cache via
/// <see cref="INavBadgeCacheInvalidator"/> after successful writes.
/// </summary>
public class IssuesArchitectureTests
{
    // ── IssuesService ────────────────────────────────────────────────────────

    [HumansFact]
    public void IssuesService_LivesInHumansApplicationServicesIssuesNamespace()
    {
        typeof(IssuesService).Namespace
            .Should().Be("Humans.Application.Services.Issues",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void IssuesService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IIssuesRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void IssuesService_TakesIssuesBadgeInvalidator()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IIssuesBadgeCacheInvalidator),
            because: "IssuesService owns the per-user actionable-count cache surfaced by NavBadgesViewComponent and must explicitly evict each affected viewer's entry on every count-shifting mutation (memory/code/viewcomponent-no-cache.md + code-review-rules.md §Cache Invalidation)");
    }

    [HumansFact]
    public void IssuesService_TakesRepository()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IIssuesRepository));
    }

    [HumansFact]
    public void IssuesService_TakesNavBadgeInvalidator()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INavBadgeCacheInvalidator),
            because: "IssuesService invalidates the nav-badge count cache after writes that can change it (submit / status change / comment post / section change) — the dependency proves the wire is in place");
    }

    [HumansFact]
    public void IssuesService_TakesCrossSectionServiceInterfaces()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "Issues resolves reporter / assignee / resolver / comment-sender display names via IUserService instead of cross-domain .Include() chains");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Issues resolves the reporter's effective notification email via IUserEmailService.GetNotificationTargetEmailsAsync — no User.UserEmails navigation");
        paramTypes.Should().Contain(typeof(IRoleAssignmentService),
            because: "Issues fans out comment notifications to section role-holders via IRoleAssignmentService.GetActiveUserIdsInRoleAsync — no direct query on the role_assignments table");
    }

    [HumansFact]
    public void IssuesService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the Issues section has no store at all");
    }

    // ── IIssuesRepository ────────────────────────────────────────────────────

    [HumansFact]
    public void IIssuesRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IIssuesRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void IssuesRepository_IsSealed()
    {
        var repoType = typeof(IssuesRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");

        typeof(IIssuesRepository).IsAssignableFrom(repoType)
            .Should().BeTrue(because: "IssuesRepository must implement IIssuesRepository");
    }
}
