using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Surveys.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Surveys.Tests;

public class SurveysBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [SurveyStatus.Draft] = "bg-warning text-dark",
        [SurveyStatus.Open] = "bg-success",
        [SurveyStatus.Closed] = "bg-secondary",
        [SurveyStatus.PendingApproval] = "bg-info text-dark",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(SurveyStatus) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
