using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
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

    // IMemoryCache check covered by ApplicationServicesTakeNoMemoryCacheRule.
    // TakesRepository check covered by pattern G (positive wiring noise).
    // Sealed-repository check covered by IRepositoryImplementationsAreSealedRule.

    [HumansFact]
    public void FeedbackService_TakesNavBadgeInvalidator()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INavBadgeCacheInvalidator),
            because: "FeedbackService invalidates the nav-badge count cache after writes that can change it (submit / status change / message post) — the dependency proves the wire is in place");
    }

    [HumansFact]
    public void FeedbackService_TakesCrossSectionServiceInterfaces()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "Feedback resolves reporter / assignee / resolver display names via IUserService.GetUserInfosAsync — UserInfo.BurnerName implements the BurnerName-first fallback per memory/architecture/burnername-is-the-display-name.md");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Feedback resolves the reporter's effective notification email via IUserEmailService.GetNotificationTargetEmailsAsync — no User.UserEmails navigation");
        paramTypes.Should().Contain(typeof(ITeamServiceRead),
            because: "Feedback resolves assigned-team names via the cross-section ITeamServiceRead surface — no FeedbackReport.AssignedToTeam navigation at query time");
    }

    [HumansFact]
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

    // Sealed-repository check covered by IRepositoryImplementationsAreSealedRule.
}
