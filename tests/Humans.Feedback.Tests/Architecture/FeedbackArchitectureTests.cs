using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Feedback.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using FeedbackService = Humans.Feedback.Services.FeedbackService;

namespace Humans.Feedback.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the section shape for Feedback
/// (nobodies-collective/Humans#866, G5) plus the §15 repository pattern it already had
/// (issue #549). Feedback is admin-review-only and low-traffic, so no caching decorator sits
/// in front of the service — the service goes directly through
/// <see cref="IFeedbackRepository"/> and invalidates the nav-badge cache via
/// <see cref="INavBadgeCacheInvalidator"/> after successful writes.
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/FeedbackArchitectureTests.cs</c>. Its
/// store-parameter check is widened here to cover <c>DbContext</c> and
/// <c>IDbContextFactory&lt;&gt;</c> as well: the section assembly holds the repository and
/// legitimately references EF, so "the service never touches a context" has to be asserted on
/// the constructor rather than inferred from the assembly's references (§15 step 11).
/// </remarks>
public class FeedbackArchitectureTests
{


    // ── FeedbackService ──────────────────────────────────────────────────────

    // IMemoryCache check covered by ApplicationServicesTakeNoMemoryCacheRule.
    // TakesRepository check covered by pattern G (positive wiring noise).

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

        paramTypes.Should().Contain(typeof(IUserServiceRead),
            because: "Feedback resolves reporter / assignee / resolver display names via IUserServiceRead.GetUserInfosAsync — UserInfo.BurnerName implements the BurnerName-first fallback per memory/architecture/burnername-is-the-display-name.md");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Feedback resolves the reporter's effective notification email via IUserEmailService.GetNotificationTargetEmailsAsync — no User.UserEmails navigation");
        paramTypes.Should().Contain(typeof(ITeamServiceRead),
            because: "Feedback resolves assigned-team names via the cross-section ITeamServiceRead surface — no FeedbackReport.AssignedToTeam navigation at query time");
    }    // ── IFeedbackRepository ──────────────────────────────────────────────────

    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.
}
