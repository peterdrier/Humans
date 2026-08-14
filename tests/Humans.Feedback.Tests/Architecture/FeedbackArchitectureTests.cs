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
    [HumansFact]
    public void OnlySectionAndResourceArePublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5). FeedbackResource is the one
        // sanctioned extra: the boot localization diagnostic discovers section resource markers
        // through GetExportedTypes(), so an internal marker is skipped in silence (§15 step 3b).
        //
        // Both controllers are internal. Shell registers SectionControllerFeatureProvider, which
        // relaxes MVC's IsPublic check for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are never
        // hand-edited (memory/process/never-hand-edit-migrations); they are excluded rather
        // than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Feedback.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
        [
            "Humans.Feedback.Contracts.IFeedbackServiceRead",
            "Humans.Feedback.FeedbackResource",
            "Humans.Feedback.Section",
        ]);
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        controllers.Should().NotBeEmpty();
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

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
    }

    [HumansFact]
    public void FeedbackService_ConstructorTakesNoEfTypeAndNoStore()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(t => typeof(DbContext).IsAssignableFrom(t),
            because: "the service goes through IFeedbackRepository; only the repository owns a DbContext");
        parameterTypes.Should().NotContain(
            t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDbContextFactory<>),
            because: "context lifetime is the repository's business (design-rules §3)");
        parameterTypes.Should().NotContain(
            t => (t.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal),
            because: "services must not depend on store abstractions (design-rules §15); the Feedback section has no store at all");
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The carve moved every Feedback_* and Enum_Feedback* key out of SharedResource, so a
        // type still injecting IStringLocalizer<SharedResource> would resolve nothing and render
        // the raw key — a 200 with degraded copy, in every language, on paths a render test tends
        // not to reach. The views are safe by construction (_ViewImports rebinds Localizer for
        // all of them); this is the guard for controllers and services.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && p.ParameterType.GetGenericArguments()[0] != typeof(FeedbackResource))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every Feedback_* key lives in FeedbackResource; resolving one through another "
                   + "set renders the key itself and no error (§15 step 3b)");
    }

    [HumansFact]
    public void AuditEntityTypesAreLiterals()
    {
        // Persisted audit discriminators, matched by exact equality when the log is read back.
        // Declaring them as literals is what makes a rename of the entity schema-inert
        // (memory/code/type-name-as-persisted-string.md).
        Humans.Feedback.Services.AuditEntityTypes.FeedbackReport.Should().Be("FeedbackReport");
    }

    // ── IFeedbackRepository ──────────────────────────────────────────────────

    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.
}
