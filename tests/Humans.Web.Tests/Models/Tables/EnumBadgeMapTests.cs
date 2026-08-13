using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.UI.Models.Tables;

namespace Humans.Web.Tests.Models.Tables;

public class EnumBadgeMapTests
{
    [HumansFact]
    public void Mapped_enum_values_get_their_registered_badge_class()
    {
        // Deliberately a Base enum. A moved section's rows reach the map through
        // EnumBadgeMap.Register from its Section.Register, which does not run in a unit
        // test — asserting on one here would be asserting on DI having been composed
        // (the Tickets rows used to be the sample, until that section's G5 move).
        EnumBadgeMap.For(SignupStatus.Confirmed).Should().Be("bg-success");
        EnumBadgeMap.For(ShiftPeriod.Build).Should().Be("bg-info");
        EnumBadgeMap.For(SignupStatus.Refused).Should().Be("bg-danger");
    }

    [HumansFact]
    public void Unmapped_enum_values_fall_back_to_secondary()
    {
        EnumBadgeMap.For(MembershipTier.Asociado).Should().Be("bg-secondary");
    }
}
