using AwesomeAssertions;
using Humans.Feedback.Domain;
using Humans.Feedback.Contracts;

namespace Humans.Feedback.Tests.Enums;

/// <summary>
/// All three are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the OLD
/// string in the column, so a rename needs a migration that UPDATEs the stored values.
/// </summary>
public class EnumStringStabilityTests
{
    [HumansFact]
    public void StringStoredFeedbackEnums_MemberNames_MustMatchExpected()
    {
        AssertNames(typeof(FeedbackCategory), ["Bug", "FeatureRequest", "Question"]);
        AssertNames(typeof(FeedbackStatus), ["Open", "Acknowledged", "Resolved", "WontFix"]);

        // Also the DB default (HasDefaultValueSql("'UserReport'")) — renaming this
        // member specifically breaks the default, not just stored history.
        AssertNames(typeof(FeedbackSource), ["UserReport", "AgentUnresolved"]);
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
