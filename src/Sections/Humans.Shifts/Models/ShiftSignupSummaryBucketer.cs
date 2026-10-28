using System.Globalization;
using System.Resources;
using Humans.Shifts.Contracts;
using NodaTime;

namespace Humans.Shifts.Models;

internal sealed record ShiftSignupSummaryBuckets(
    List<ShiftSignupsRow> Upcoming,
    List<ShiftSignupsRow> Pending,
    List<ShiftSignupsRow> Past);

/// <summary>
/// <c>ShiftSignupBucketer</c>'s split over the leaf summary, for the public card. Refused and Cancelled signups are
/// not history worth showing.
/// </summary>
internal static class ShiftSignupSummaryBucketer
{
    private static readonly ResourceManager Resources = new(typeof(ShiftsResource));

    public static ShiftSignupSummaryBuckets Build(
        IReadOnlyList<ShiftSignupSummary> signups,
        IReadOnlyDictionary<Guid, string> teamNames,
        Instant now)
    {
        var upcoming = new List<ShiftSignupsRow>();
        var pending = new List<ShiftSignupsRow>();
        var past = new List<ShiftSignupsRow>();

        foreach (var signup in signups)
        {
            var row = new ShiftSignupsRow(
                signup,
                teamNames.GetValueOrDefault(signup.TeamId, Resources.GetString("Shifts_Signup_UnknownTeam", CultureInfo.CurrentUICulture)!));

            switch (signup.Status)
            {
                case SignupStatus.Confirmed when signup.AbsoluteEnd > now:
                    upcoming.Add(row);
                    break;
                case SignupStatus.Pending:
                    pending.Add(row);
                    break;
                default:
                    if (signup.Status is SignupStatus.Confirmed or SignupStatus.NoShow or SignupStatus.Bailed)
                        past.Add(row);
                    break;
            }
        }

        return new ShiftSignupSummaryBuckets(
            upcoming.OrderBy(r => r.Signup.AbsoluteStart).ToList(),
            pending.OrderBy(r => r.Signup.AbsoluteStart).ToList(),
            past.OrderByDescending(r => r.Signup.AbsoluteStart).ToList());
    }
}
