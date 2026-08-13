using AwesomeAssertions;
using Humans.Feedback.Domain;

namespace Humans.Feedback.Tests.Enums;

/// <summary>
/// Feedback's string-stored-enum guard. Lives here rather than the central
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> because <see cref="FeedbackCategory"/>,
/// <see cref="FeedbackStatus"/> and <see cref="FeedbackSource"/> are all internal to
/// <c>Humans.Feedback</c> after the section's G5 move (nobodies-collective/Humans#866), so the
/// central project cannot name them (nobodies-collective/Humans#1025).
/// </summary>
/// <remarks>
/// All three are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
/// the OLD string in <c>feedback_reports.category</c> / <c>.status</c> / <c>.source</c>. A
/// rename needs a migration that UPDATEs the stored values.
/// <see cref="FeedbackSource"/> is doubly load-bearing: it also has a DB default of
/// <c>'UserReport'</c> (<c>HasDefaultValueSql</c>), so renaming that specific member breaks
/// the default, not just stored history. A <c>[Fact]</c> rather than a theory: a public test
/// method cannot take an internal enum type as a parameter (CS0051, Issues' fold).
/// </remarks>
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
