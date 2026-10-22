using AwesomeAssertions;
using Humans.Issues.Contracts;

namespace Humans.Issues.Tests.Enums;

/// <summary>
/// Both are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the OLD
/// string in the column, so a rename needs a migration that UPDATEs the stored values.
/// </summary>
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
