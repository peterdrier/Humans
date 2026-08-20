using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Models.Tables;
using Humans.Users.Contracts;
using NSubstitute;
using Serilog;

namespace Humans.Web.Tests.Models.Tables;

public class EnumBadgeMapTests
{
    [HumansFact]
    public void Mapped_enum_values_get_their_registered_badge_class()
    {
        // Deliberately Base enums. A moved section's rows reach the map through
        // EnumBadgeMap.Register from its Section.Register, which does not run in a unit
        // test — asserting on one here would be asserting on DI having been composed.
        // (The ShiftPeriod/SignupStatus rows were the sample until Shifts pushed them in
        // from its own Section.Register — G5 lane 4b-i, nobodies-collective/Humans#866 —
        // and they are pinned by ShiftsArchitectureTests now.)
        EnumBadgeMap.For(EmailOutboxStatus.Queued).Should().Be("bg-warning text-dark");
        EnumBadgeMap.For(EmailOutboxStatus.Sent).Should().Be("bg-success");
        EnumBadgeMap.For(EmailOutboxStatus.Failed).Should().Be("bg-danger");
    }

    [HumansFact]
    public void Unmapped_enum_values_fall_back_to_secondary()
    {
        EnumBadgeMap.For(MembershipTier.Asociado).Should().Be("bg-secondary");
    }

    // ── issue #1065: unmapped values log a warning naming the enum and value ────

    [HumansFact]
    public void Unmapped_enum_values_log_a_warning_naming_the_type_and_value()
    {
        var logger = Substitute.For<ILogger>();
        EnumBadgeMap.SetLoggerForTests(logger);
        try
        {
            EnumBadgeMap.For(MembershipTier.Colaborador);

            logger.Received(1).Warning(
                Arg.Is<string>(m => m.Contains("EnumBadgeMap.For") && m.Contains("{EnumType}") && m.Contains("{Value}")),
                Arg.Is<object[]>(a => a.Length == 2
                    && (string)a[0] == nameof(MembershipTier)
                    && Equals(a[1], MembershipTier.Colaborador)));
        }
        finally
        {
            EnumBadgeMap.SetLoggerForTests(Log.ForContext(typeof(EnumBadgeMap)));
        }
    }

    [HumansFact]
    public void Mapped_enum_values_do_not_log_a_warning()
    {
        var logger = Substitute.For<ILogger>();
        EnumBadgeMap.SetLoggerForTests(logger);
        try
        {
            EnumBadgeMap.For(EmailOutboxStatus.Queued);

            logger.DidNotReceiveWithAnyArgs().Warning(default!, default(object[])!);
        }
        finally
        {
            EnumBadgeMap.SetLoggerForTests(Log.ForContext(typeof(EnumBadgeMap)));
        }
    }
}
