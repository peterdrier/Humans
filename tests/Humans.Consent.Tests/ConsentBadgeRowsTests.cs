using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Consent.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Consent.Tests;

public class ConsentBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [ConsentCheckStatus.Pending] = "bg-warning text-dark",
        [ConsentCheckStatus.Cleared] = "bg-success",
        [ConsentCheckStatus.Flagged] = "bg-danger",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(ConsentCheckStatus) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
