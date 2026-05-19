using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Shifts;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Shift" — humans who have signed up for at least one shift
/// in the active EventSettings event. Pending and Confirmed signups count;
/// Refused/Bailed/Cancelled/NoShow do not (see
/// <see cref="IShiftSignupService.GetActiveCommittedUserIdsForEventAsync"/>).
/// </summary>
public sealed class HasShiftAudience(
    IShiftSignupService shiftSignups,
    IShiftManagementService shiftManagement) : IMailerAudience
{
    public string Key => "has-shift";
    public string DisplayName => "Volunteers with a shift signup";
    public string MailerLiteGroupName => "Humans - Has Shift";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var activeEvent = await shiftManagement.GetActiveAsync();
        if (activeEvent is null) return new HashSet<Guid>();

        return await shiftSignups.GetActiveCommittedUserIdsForEventAsync(activeEvent.Id, ct);
    }
}
