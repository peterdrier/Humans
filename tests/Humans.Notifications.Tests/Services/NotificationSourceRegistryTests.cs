using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Notifications.Services;
using Xunit;

namespace Humans.Notifications.Tests.Services;

public class NotificationSourceRegistryTests
{
    private sealed class Stray : NotificationSourceContributor
    {
        public static readonly NotificationSourceDefinition SameName = new("TestPlain", MessageCategory.Marketing);
    }

    private sealed class TooLong : NotificationSourceContributor
    {
        public static readonly NotificationSourceDefinition Name = new(new string('x', 51), MessageCategory.System);
    }

    [HumansFact]
    public void GroupsAndRetiredComeFromTheDefinitions()
    {
        var registry = TestSources.Registry;

        registry.NamesIn(NotificationInboxGroup.Shifts).Should().Equal(TestSources.ShiftUpdate.Name);
        registry.NamesIn(NotificationInboxGroup.Approvals)
            .Should().BeEquivalentTo([TestSources.Approval.Name, TestSources.Retired.Name]);
        registry.RetiredNames.Should().BeEquivalentTo([TestSources.Retired.Name, TestSources.RetiredToo.Name]);
    }

    [HumansFact]
    public void TheContributedListIsTheStaticFields()
    {
        new TestSources().Sources.Should().HaveCount(6).And.Contain(TestSources.RetiredToo);
    }

    [HumansFact]
    public void ANameDeclaredTwiceFailsStartup()
    {
        var act = () => new NotificationSourceRegistry([new TestSources(), new Stray()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*TestPlain*");
    }

    [HumansFact]
    public void ANameLongerThanTheColumnFailsStartup()
    {
        var act = () => new NotificationSourceRegistry([new TooLong()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*50*");
    }
}
