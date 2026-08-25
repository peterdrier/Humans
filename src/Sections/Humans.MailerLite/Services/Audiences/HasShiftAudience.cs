using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// "Humans - Has Shift" — humans with at least one Pending/Confirmed signup
/// in the active event (<see cref="ShiftUserSummary.HasShift"/>).
/// </summary>
internal sealed class HasShiftAudience(
    IShiftView shiftView,
    IUserServiceRead users) : ShiftViewAudienceBase(shiftView, users)
{
    public override string Key => "has-shift";
    public override string DisplayName => "Volunteers with a shift signup";
    public override string MailerLiteGroupName => "Humans - Has Shift";

    protected override bool Matches(ShiftUserSummary summary) => summary.HasShift;
}
