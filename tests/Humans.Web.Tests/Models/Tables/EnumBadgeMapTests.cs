using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.Web.Models.Tables;

namespace Humans.Web.Tests.Models.Tables;

public class EnumBadgeMapTests
{
    [HumansFact]
    public void Mapped_enum_values_get_their_registered_badge_class()
    {
        EnumBadgeMap.For(TicketAttendeeStatus.Valid).Should().Be("bg-success");
        EnumBadgeMap.For(TicketAttendeeStatus.CheckedIn).Should().Be("bg-info");
        EnumBadgeMap.For(TicketAttendeeStatus.Void).Should().Be("bg-danger");
    }

    [HumansFact]
    public void Unmapped_enum_values_fall_back_to_secondary()
    {
        EnumBadgeMap.For(StoreOrderCounterpartyType.Team).Should().Be("bg-secondary");
    }
}
