using AwesomeAssertions;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Tests.Enums;

/// <summary>
/// Issues' string-stored-enum guard. Lives with the section that owns
/// <see cref="IssueStatus"/> and <see cref="IssueCategory"/>, not in a central test project.
/// </summary>
/// <remarks>
/// Both are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the
/// OLD string in <c>issues.status</c> / <c>issues.category</c>. A rename needs a migration
/// that UPDATEs the stored values. <see cref="IssueCategory"/> is doubly load-bearing: it is
/// also parsed by name across a section boundary via <c>Enum.TryParse&lt;IssueCategory&gt;</c>
/// in <c>Humans.Agent</c>'s <c>AgentService.ParseIssueProposalArgs</c> — renaming a member
/// silently breaks that parse (it falls back to <see cref="IssueCategory.Question"/> instead
/// of erroring), not just stored history.
/// </remarks>
public class EnumStringStabilityTests
{
    [HumansFact]
    public void StringStoredIssuesEnums_MemberNames_MustMatchExpected()
    {
        AssertNames(typeof(IssueStatus),
            ["Triage", "Open", "InProgress", "Resolved", "WontFix", "Duplicate"]);

        // Also parsed by name across a section boundary (Humans.Agent's
        // Enum.TryParse<IssueCategory> in AgentService.ParseIssueProposalArgs) — a rename
        // here silently breaks that cross-section parse, not just stored history.
        AssertNames(typeof(IssueCategory), ["Bug", "Feature", "Question"]);
    }

    private static void AssertNames(Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);
        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                "If you renamed it, create a DB migration to UPDATE the old values.");
        }
    }
}
