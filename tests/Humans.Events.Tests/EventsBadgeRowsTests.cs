using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Events.Contracts;
using Humans.Events.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Events.Tests;

public class EventsBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [EventStatus.Draft] = "bg-secondary",
        [EventStatus.Pending] = "bg-warning text-dark",
        [EventStatus.Approved] = "bg-success",
        [EventStatus.Rejected] = "bg-danger",
        [EventStatus.ResubmitRequested] = "bg-info",
        [EventStatus.Withdrawn] = "bg-dark",
        [EventModerationActionType.Approved] = "bg-success",
        [EventModerationActionType.Rejected] = "bg-danger",
        [EventModerationActionType.ResubmitRequested] = "bg-info",
        [EventModerationActionType.Edited] = "bg-primary",
        [EventModerationActionType.Withdrawn] = "bg-secondary",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(EventStatus), typeof(EventModerationActionType) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
