using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

internal sealed class HasShiftEventAudience(
    IShiftView shiftView,
    IUserService users,
    ICommunicationPreferenceService prefs) : ShiftViewAudienceBase(shiftView, users, prefs)
{
    public override string Key => "has-shift-event";
    public override string DisplayName => "Volunteers with an event shift";
    public override string MailerLiteGroupName => "Humans - Has Shift - Event";

    protected override bool Matches(ShiftUserSummary summary) =>
        summary.HasShiftInPeriod(ShiftPeriod.Event);
}
