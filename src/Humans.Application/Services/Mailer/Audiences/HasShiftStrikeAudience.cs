using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Shift - Strike" — humans with at least one Pending/Confirmed
/// signup on a Strike-period shift (after the event ends) in the active event.
/// </summary>
public sealed class HasShiftStrikeAudience(
    IShiftView shiftView,
    IUserServiceRead users) : HasShiftInPeriodAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-strike";
    public override string DisplayName => "Volunteers with a strike shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Strike";

    protected override ShiftPeriod Period => ShiftPeriod.Strike;
}
