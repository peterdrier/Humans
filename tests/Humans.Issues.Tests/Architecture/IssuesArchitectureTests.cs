using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Services;
using IssuesService = Humans.Issues.Services.IssuesService;
using Humans.Issues.Contracts;

namespace Humans.Issues.Tests.Architecture;

/// <summary>
/// Constructor-dependency checks for <see cref="IssuesService"/>. They prove a
/// dependency is wired, not that it is used; which mutations invalidate which
/// cache is pinned in IssuesServiceTests.
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
            because: "IssuesService evicts the per-user actionable counts through this invalidator (memory/code/viewcomponent-no-cache.md)");
    }

    [HumansFact]
    public void IssuesService_TakesNavBadgeInvalidator()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INavBadgeCacheInvalidator),
            because: "IssuesService invalidates the nav-badge count through this invalidator");
    }

    [HumansFact]
    public void IssuesService_TakesCrossSectionServiceInterfaces()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserServiceRead),
            because: "Issues reads user display names through the read-only user interface");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Issues reads the reporter's notification email through the Users section");
        paramTypes.Should().Contain(typeof(IRoleAssignmentServiceRead),
            because: "Issues reads section role-holders through the read-only role interface");
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
    // MA0053 (an unsealed internal class is a build error).
}
