using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// "Humans - Has Shift - Setup" — humans with at least one Pending/Confirmed
/// signup on a Build-period shift (before gates open) in the active event.
/// </summary>
internal sealed class HasShiftSetupAudience(
    IShiftView shiftView,
    IUserServiceRead users) : ShiftViewAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-setup";
    public override string DisplayName => "Volunteers with a setup shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Setup";

    protected override bool Matches(ShiftUserSummary summary) =>
        summary.HasShiftInPeriod(ShiftPeriod.Build);
}
