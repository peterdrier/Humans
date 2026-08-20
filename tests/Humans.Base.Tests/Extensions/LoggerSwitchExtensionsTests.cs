using AwesomeAssertions;
using Humans.Base.Extensions;
using Microsoft.Extensions.Logging;

namespace Humans.Base.Tests.Extensions;

public class LoggerSwitchExtensionsTests
{
    private enum SampleEnum { Known, Unknown }

    [HumansFact]
    public void SwitchDefaultWarn_logs_a_warning_naming_the_type_value_and_site()
    {
        var logger = new CapturingLogger<LoggerSwitchExtensionsTests>();

        logger.SwitchDefaultWarn(SampleEnum.Unknown, site: "SomeMethod");

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("SomeMethod")
            .And.Contain(nameof(SampleEnum))
            .And.Contain(nameof(SampleEnum.Unknown));
    }

    [HumansFact]
    public void SwitchDefaultWarn_defaults_site_to_the_caller_member_name()
    {
        var logger = new CapturingLogger<LoggerSwitchExtensionsTests>();

        logger.SwitchDefaultWarn(SampleEnum.Unknown);

        logger.Entries.Should().ContainSingle()
            .Which.Message.Should().Contain(nameof(SwitchDefaultWarn_defaults_site_to_the_caller_member_name));
    }
}
