using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Issues.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Issues.Tests;

public class IssuesBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [IssueStatus.Triage] = "bg-warning text-dark",
        [IssueStatus.Open] = "bg-primary text-white",
        [IssueStatus.InProgress] = "bg-info text-dark",
        [IssueStatus.Resolved] = "bg-success text-white",
        [IssueStatus.WontFix] = "bg-secondary text-white",
        [IssueStatus.Duplicate] = "bg-secondary text-white",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(IssueStatus) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
