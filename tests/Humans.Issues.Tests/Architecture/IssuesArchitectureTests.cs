using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Services;
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
            because: "IssuesService owns the per-user actionable-count cache surfaced by IssuesUserMenuViewComponent and must explicitly evict each affected viewer's entry on every count-shifting mutation (memory/code/viewcomponent-no-cache.md + code-review-rules.md §Cache Invalidation)");
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
    public void AuditEntityTypesAreLiterals()
    {
        // These are literal string values we store in the DB. Pinned so a rename can't
        // quietly change them and orphan existing audit_log rows
        // (memory/code/type-name-as-persisted-string.md).
        AuditEntityTypes.Issue.Should().Be("Issue");
    }

    // ── IIssuesRepository ────────────────────────────────────────────────────

    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.
}
