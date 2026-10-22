using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

internal sealed class HasShiftAudience(
    IShiftView shiftView,
    IUserService users,
    ICommunicationPreferenceService prefs) : ShiftViewAudienceBase(shiftView, users, prefs)
{
    public override string Key => "has-shift";
    public override string DisplayName => "Volunteers with a shift signup";
    public override string MailerLiteGroupName => "Humans - Has Shift";

    protected override bool Matches(ShiftUserSummary summary) => summary.HasShift;
}
