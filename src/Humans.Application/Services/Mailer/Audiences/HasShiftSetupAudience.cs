using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Shift - Setup" — humans with at least one Pending/Confirmed
/// signup on a Build-period shift (before gates open) in the active event.
/// </summary>
public sealed class HasShiftSetupAudience(
    IShiftView shiftView,
    IUserServiceRead users) : HasShiftInPeriodAudienceBase(shiftView, users)
{
    public override string Key => "has-shift-setup";
    public override string DisplayName => "Volunteers with a setup shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Setup";

    protected override ShiftPeriod Period => ShiftPeriod.Build;
}
