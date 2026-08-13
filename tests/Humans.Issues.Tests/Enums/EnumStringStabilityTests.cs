using AwesomeAssertions;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Tests.Enums;

/// <summary>
/// Issues' string-stored-enum guard. Lives here rather than the central
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> because <see cref="IssueStatus"/>
/// is internal to <c>Humans.Issues</c> and <see cref="IssueCategory"/> sits on Issues'
/// contracts leaf after the section's G5 move (nobodies-collective/Humans#866), neither of
/// which the central project can name (nobodies-collective/Humans#1025).
/// </summary>
/// <remarks>
/// Both are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the
/// OLD string in <c>issues.status</c> / <c>issues.category</c>. A rename needs a migration
/// that UPDATEs the stored values. <see cref="IssueCategory"/> is doubly load-bearing: it is
/// also parsed by name across a section boundary via <c>Enum.TryParse&lt;IssueCategory&gt;</c>
/// in <c>Humans.Agent</c>'s <c>AgentService.ParseIssueProposalArgs</c> — renaming a member
/// silently breaks that parse (it falls back to <see cref="IssueCategory.Question"/> instead
/// of erroring), not just stored history. A <c>[Fact]</c> rather than a theory: a public test
/// method cannot take an internal enum type as a parameter (CS0051).
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
