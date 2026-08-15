using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Issues.Data;
using Humans.Issues.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using IssuesService = Humans.Issues.Services.IssuesService;

namespace Humans.Issues.Tests.Architecture;

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
    public void IssuesService_TakesIssuesBadgeInvalidator()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IIssuesBadgeCacheInvalidator),
            because: "IssuesService owns the per-user actionable-count cache surfaced by NavBadgesViewComponent and must explicitly evict each affected viewer's entry on every count-shifting mutation (memory/code/viewcomponent-no-cache.md + code-review-rules.md §Cache Invalidation)");
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

        paramTypes.Should().Contain(typeof(IUserServiceRead),
            because: "Issues resolves reporter / assignee / resolver / comment-sender display names via IUserServiceRead instead of taking the write-capable user service or using cross-domain .Include() chains");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Issues resolves the reporter's effective notification email via IUserEmailService.GetNotificationTargetEmailsAsync — no User.UserEmails navigation");
        paramTypes.Should().Contain(typeof(IRoleAssignmentService),
            because: "Issues fans out comment notifications to section role-holders via IRoleAssignmentService.GetActiveUserIdsInRoleAsync — no direct query on the role_assignments table");
    }

    [HumansFact]
    public void IssuesService_ConstructorTakesNoEfType()
    {
        // Replaces the pre-move assertion that typeof(IssuesService).Assembly carries no
        // EF reference: that was a true statement about Humans.Application, and the section
        // assembly holds the repository and references EF on purpose (§15 step 11). The
        // constructor is what the original was reaching for, and it is the stronger check.
        var parameterTypes = typeof(IssuesService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(t => typeof(DbContext).IsAssignableFrom(t),
            because: "the service goes through IIssuesRepository; only the repository owns a DbContext");
        parameterTypes.Should().NotContain(
            t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDbContextFactory<>),
            because: "context lifetime is the repository's business (design-rules §3)");
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The carve moved every Issue_* and Enum_IssueCategory_* key out of SharedResource, so a
        // type still injecting IStringLocalizer<SharedResource> would resolve nothing and render
        // the raw key — a 200 with degraded copy, in every language, on paths a render test tends
        // not to reach. The views are safe by construction (_ViewImports rebinds Localizer for
        // all of them); this is the guard for controllers and services.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && p.ParameterType.GetGenericArguments()[0] != typeof(IssuesResource))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every Issue_* key lives in IssuesResource; resolving one through another "
                   + "set renders the key itself and no error (§15 step 3b)");
    }

    [HumansFact]
    public void AuditEntityTypesAreLiterals()
    {
        // Persisted audit discriminators, matched by exact equality when the log is read back.
        // Declaring them as literals is what makes a rename of the entity schema-inert
        // (memory/code/type-name-as-persisted-string.md).
        AuditEntityTypes.Issue.Should().Be("Issue");
    }

    // ── IIssuesRepository ────────────────────────────────────────────────────

    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.
}
