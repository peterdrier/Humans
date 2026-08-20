using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// "Humans - Has Shift - Event" — humans with at least one Pending/Confirmed
/// signup on an Event-period shift (during the event) in the active event.
/// </summary>
internal sealed class HasShiftEventAudience(
    IShiftView shiftView,
    IUserServiceRead users) : HasShiftInPeriodAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-event";
    public override string DisplayName => "Volunteers with an event shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Event";

    protected override ShiftPeriod Period => ShiftPeriod.Event;
}
