using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.GoogleIntegration.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.GoogleIntegration.Tests;

public class GoogleIntegrationBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [GoogleEmailStatus.Unknown] = "bg-secondary",
        [GoogleEmailStatus.Valid] = "bg-success",
        [GoogleEmailStatus.Rejected] = "bg-danger",
        [SyncMode.None] = "bg-secondary",
        [SyncMode.AddOnly] = "bg-info",
        [SyncMode.AddAndRemove] = "bg-success",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(GoogleEmailStatus), typeof(SyncMode) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
