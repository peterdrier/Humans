using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Shift - Event" — humans with at least one Pending/Confirmed
/// signup on an Event-period shift (during the event) in the active event.
/// </summary>
public sealed class HasShiftEventAudience(
    IShiftView shiftView,
    IUserServiceRead users) : HasShiftInPeriodAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-event";
    public override string DisplayName => "Volunteers with an event shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Event";

    protected override ShiftPeriod Period => ShiftPeriod.Event;
}
