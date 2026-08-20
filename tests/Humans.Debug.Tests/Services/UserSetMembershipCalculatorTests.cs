using AwesomeAssertions;
using Humans.Debug.Services;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using NodaTime;
using NSubstitute;

namespace Humans.Debug.Tests.Services;

/// <summary>
/// Unit tests for the Venn/UpSet set-membership mask math, extracted from the deleted
/// admin-dashboard aggregator at nobodies-collective/Humans#1091.
/// </summary>
public class UserSetMembershipCalculatorTests
{
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();

    public UserSetMembershipCalculatorTests()
    {
        _burnSettings.GetActiveAsync().Returns(MakeBurnSettings(2026));
    }

    [HumansFact]
    public async Task BuildAsync_places_each_user_in_exactly_one_bucket_by_bit_mask()
    {
        // u1: profile + ticket (mask 3). u2: shift only (mask 4). u3: nothing (mask 0).
        var u1 = MakeUserInfo(hasProfile: true, ticketYear: 2026, marketingOptedOut: null);
        var u2 = MakeUserInfo(hasProfile: false, ticketYear: null, marketingOptedOut: null);
        var u3 = MakeUserInfo(hasProfile: false, ticketYear: null, marketingOptedOut: null);
        var snapshot = new List<UserInfo> { u1, u2, u3 };

        StubShifts(hasShift: [u2.Id]);

        var result = await UserSetMembershipCalculator.BuildAsync(
            snapshot, _burnSettings, _shiftView, Xunit.TestContext.Current.CancellationToken);

        result.TotalUsers.Should().Be(3);
        result.CountsByMask[3].Should().Be(1); // Profile | Ticket
        result.CountsByMask[4].Should().Be(1); // Shift
        result.CountsByMask[0].Should().Be(1); // none
    }

    [HumansFact]
    public async Task BuildAsync_explicit_opt_in_only_sets_marketing_bit()
    {
        // Tri-state: null (no preference row) and true (opted out) must NOT set the bit;
        // only explicit false (opted in) does.
        var noPreference = MakeUserInfo(hasProfile: false, ticketYear: null, marketingOptedOut: null);
        var optedOut = MakeUserInfo(hasProfile: false, ticketYear: null, marketingOptedOut: true);
        var optedIn = MakeUserInfo(hasProfile: false, ticketYear: null, marketingOptedOut: false);
        var snapshot = new List<UserInfo> { noPreference, optedOut, optedIn };

        StubShifts(hasShift: []);

        var result = await UserSetMembershipCalculator.BuildAsync(
            snapshot, _burnSettings, _shiftView, Xunit.TestContext.Current.CancellationToken);

        result.MarketingOptInsCount.Should().Be(1);
    }

    [HumansFact]
    public async Task BuildAsync_marginal_and_intersection_counts_sum_across_buckets()
    {
        var profileTicketShift = MakeUserInfo(hasProfile: true, ticketYear: 2026, marketingOptedOut: null);
        var profileOnly = MakeUserInfo(hasProfile: true, ticketYear: null, marketingOptedOut: null);
        var snapshot = new List<UserInfo> { profileTicketShift, profileOnly };

        StubShifts(hasShift: [profileTicketShift.Id]);

        var result = await UserSetMembershipCalculator.BuildAsync(
            snapshot, _burnSettings, _shiftView, Xunit.TestContext.Current.CancellationToken);

        result.ProfilesCount.Should().Be(2);
        result.TicketsCount.Should().Be(1);
        result.ShiftsCount.Should().Be(1);
        result.ProfileTicketShiftIntersection.Should().Be(1);
    }

    private void StubShifts(IReadOnlyCollection<Guid> hasShift) =>
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = (IEnumerable<Guid>)ci[0];
                IReadOnlyDictionary<Guid, ShiftUserSummary> dict = ids.ToDictionary(
                    id => id,
                    id => hasShift.Contains(id)
                        ? new ShiftUserSummary(id, [], [MakeActiveSignup()])
                        : ShiftUserSummary.Empty(id));
                return new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(dict);
            });

    private static ShiftSignupSummary MakeActiveSignup() => new(
        Id: Guid.NewGuid(),
        ShiftId: Guid.NewGuid(),
        SignupBlockId: null,
        Status: SignupStatus.Confirmed,
        RotaId: Guid.NewGuid(),
        RotaName: "Rota",
        RotaDescription: null,
        RotaPracticalInfo: null,
        TeamId: Guid.NewGuid(),
        EventSettingsId: Guid.NewGuid(),
        Date: new LocalDate(2026, 8, 1),
        Period: ShiftPeriod.Event,
        IsAllDay: true,
        WindowStart: LocalTime.Midnight,
        WindowEnd: LocalTime.Midnight,
        DurationHours: 8,
        ShiftDescription: null,
        AbsoluteStart: Instant.FromUtc(2026, 8, 1, 0, 0),
        AbsoluteEnd: Instant.FromUtc(2026, 8, 1, 8, 0));

    private static BurnSettingsInfo MakeBurnSettings(int year) => new(
        Id: Guid.NewGuid(),
        EventName: "Nowhere " + year,
        Year: year,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(year, 7, 1),
        BuildStartOffset: -7,
        EventEndOffset: 5,
        StrikeEndOffset: 8,
        FirstCrewStartOffset: -10,
        SetupWeekStartOffset: -7,
        PreEventWeekStartOffset: -3,
        FinishingWeekendStartOffset: 6,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    private static UserInfo MakeUserInfo(bool hasProfile, int? ticketYear, bool? marketingOptedOut)
    {
        var id = Guid.NewGuid();
        var profile = hasProfile
            ? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = "Dusty",
                FirstName = "Dusty",
                LastName = "Rhoads",
            }
            : null;

        var participations = ticketYear.HasValue
            ? new List<EventParticipation>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = id,
                    Year = ticketYear.Value,
                    Status = ParticipationStatus.Ticketed,
                    Source = ParticipationSource.AdminBackfill,
                },
            }
            : [];

        var communicationPreferences = marketingOptedOut.HasValue
            ? new List<CommunicationPreference>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = id,
                    Category = MessageCategory.Marketing,
                    OptedOut = marketingOptedOut.Value,
                    UpdateSource = "test",
                },
            }
            : [];

        return UserInfo.Create(
            user: new User
            {
                Id = id,
                DisplayName = "U",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            userEmails: [],
            eventParticipations: participations,
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: communicationPreferences);
    }
}
