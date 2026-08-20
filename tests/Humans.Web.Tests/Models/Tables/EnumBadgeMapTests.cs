using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Models.Tables;
using Humans.Users.Contracts;

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
}
