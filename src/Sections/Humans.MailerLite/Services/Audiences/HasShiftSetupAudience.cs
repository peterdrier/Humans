using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

internal sealed class HasShiftSetupAudience(
    IShiftView shiftView,
    IUserService users,
    ICommunicationPreferenceService prefs) : ShiftViewAudienceBase(shiftView, users, prefs)
{
    public override string Key => "has-shift-setup";
    public override string DisplayName => "Volunteers with a setup shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Setup";

    protected override bool Matches(ShiftUserSummary summary) =>
        summary.HasShiftInPeriod(ShiftPeriod.Build);
}
