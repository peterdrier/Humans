using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Issues.Services;
using IssuesService = Humans.Issues.Services.IssuesService;

namespace Humans.Issues.Tests.Architecture;

/// <summary>
/// Constructor-dependency checks for <see cref="IssuesService"/>. They prove a
/// dependency is wired, not that it is used; which mutations invalidate which
/// cache is pinned in IssuesServiceTests.
/// </summary>
public class IssuesArchitectureTests
{
    [HumansFact]
    public void IssuesService_TakesCrossSectionServiceInterfaces()
    {
        var ctor = typeof(IssuesService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "Issues reads user display names through the read-only user interface");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "Issues reads the reporter's notification email through the Users section");
        paramTypes.Should().Contain(typeof(IRoleAssignmentService),
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
}
