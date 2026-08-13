using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Users.Contracts;

namespace Humans.Mailer.Services.Audiences;

/// <summary>
/// "Humans - Has Shift - Setup" — humans with at least one Pending/Confirmed
/// signup on a Build-period shift (before gates open) in the active event.
/// </summary>
internal sealed class HasShiftSetupAudience(
    IShiftView shiftView,
    IUserServiceRead users) : HasShiftInPeriodAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-setup";
    public override string DisplayName => "Volunteers with a setup shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Setup";

    protected override ShiftPeriod Period => ShiftPeriod.Build;
}
