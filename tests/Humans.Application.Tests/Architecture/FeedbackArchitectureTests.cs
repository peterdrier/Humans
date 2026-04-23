using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Feedback;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FeedbackService = Humans.Application.Services.Feedback.FeedbackService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Feedback
/// section — migrated per issue #549. Feedback is admin-review-only and
/// low-traffic, so no caching decorator sits in front of the service — the
/// service goes directly through <see cref="IFeedbackRepository"/> and
/// invalidates the nav-badge cache via <see cref="INavBadgeCacheInvalidator"/>
/// after successful writes.
/// </summary>
public class FeedbackArchitectureTests
{
    // ── FeedbackService ──────────────────────────────────────────────────────

    [Fact]
    public void FeedbackService_LivesInHumansApplicationServicesFeedbackNamespace()
    {
        typeof(FeedbackService).Namespace
            .Should().Be("Humans.Application.Services.Feedback",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void FeedbackService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IFeedbackRepository instead (design-rules §3)");
    }

    [Fact]
    public void FeedbackService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "Feedback has no canonical domain cache; cross-cutting nav-badge invalidation goes through INavBadgeCacheInvalidator, not IMemoryCache directly (design-rules §5)");
    }

    [Fact]
    public void FeedbackService_TakesRepository()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IFeedbackRepository));
    }

    [Fact]
    public void FeedbackService_TakesNavBadgeInvalidator()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INavBadgeCacheInvalidator),
            because: "FeedbackService invalidates the nav-badge count cache after writes that can change it (submit / status change / message post) — the dependency proves the wire is in place");
    }

    [Fact]
    public void FeedbackService_TakesCrossSectionServiceInterfaces()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "Feedback resolves reporter / assignee / resolver display names via IUserService instead of cross-domain .Include() chains");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Feedback resolves the reporter's effective notification email via IUserEmailService.GetNotificationTargetEmailsAsync — no User.UserEmails navigation");
        paramTypes.Should().Contain(typeof(ITeamService),
            because: "Feedback resolves assigned-team names via ITeamService.GetTeamNamesByIdsAsync — no FeedbackReport.AssignedToTeam navigation at query time");
    }

    [Fact]
    public void FeedbackService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the Feedback section has no store at all");
    }

    // ── IFeedbackRepository ──────────────────────────────────────────────────

    [Fact]
    public void IFeedbackRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IFeedbackRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void FeedbackRepository_IsSealed()
    {
        var repoType = typeof(FeedbackRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");

        typeof(IFeedbackRepository).IsAssignableFrom(repoType)
            .Should().BeTrue(because: "FeedbackRepository must implement IFeedbackRepository");
    }
}
