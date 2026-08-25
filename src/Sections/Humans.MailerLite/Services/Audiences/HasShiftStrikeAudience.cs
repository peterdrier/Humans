using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// "Humans - Has Shift - Strike" — humans with at least one Pending/Confirmed
/// signup on a Strike-period shift (after the event ends) in the active event.
/// </summary>
internal sealed class HasShiftStrikeAudience(
    IShiftView shiftView,
    IUserServiceRead users) : ShiftViewAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-strike";
    public override string DisplayName => "Volunteers with a strike shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Strike";

    protected override bool Matches(ShiftUserSummary summary) =>
        summary.HasShiftInPeriod(ShiftPeriod.Strike);
}
