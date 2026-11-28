using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tasks.Tests;

public class TasksBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [TaskItemStatus.Todo] = "bg-light text-dark",
        [TaskItemStatus.InProgress] = "bg-info",
        [TaskItemStatus.Blocked] = "bg-warning text-dark",
        [TaskItemStatus.Done] = "bg-success",
        [TaskItemStatus.Cancelled] = "bg-secondary",
        [TaskTag.Understaffed] = "bg-danger",
        [TaskTag.Waiting] = "bg-warning text-dark",
        [TaskTag.NeedsApproval] = "bg-light text-dark border",
        [TaskTag.Hidden] = "bg-light text-dark border",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var member in Enum.GetValues<TaskItemStatus>().Cast<Enum>().Concat(Enum.GetValues<TaskTag>().Cast<Enum>()))
            Expected.Should().ContainKey(member, because: $"{member.GetType().Name}.{member} needs a badge row, not the grey fallback");
    }
}
