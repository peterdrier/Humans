using Humans.Shifts.Contracts;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Shifts.Models;

internal sealed record ShiftSignupSummaryBuckets(
    List<ShiftSignupsRow> Upcoming,
    List<ShiftSignupsRow> Pending,
    List<ShiftSignupsRow> Past);

/// <summary>
/// Buckets a user's flat signup projection into upcoming / pending / past for the
/// <c>&lt;vc:shift-signups&gt;</c> card.
/// </summary>
/// <remarks>
/// The same three-way split <c>ShiftSignupBucketer</c> applies to the entity-bearing
/// rows the /Shifts/Mine page binds, over <see cref="ShiftSignupSummary"/> instead —
/// the card's component is public and therefore reads the leaf
/// (<c>Contracts/ShiftSignupsViewComponent.cs</c>). Matches the entity bucketer's
/// <c>includeOtherStatusesInPast: false</c> mode, which is the only mode the card ever
/// used: Refused and Cancelled signups are not history worth showing.
/// </remarks>
internal static class ShiftSignupSummaryBucketer
{
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
                teamNames.GetValueOrDefault(signup.TeamId, "Unknown"));

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
