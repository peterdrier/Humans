using System.Text.Json;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Cantina;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Cantina;

/// <summary>
/// Unit tests for <see cref="CantinaRosterService"/>. The on-site cohort comes
/// from <see cref="IShiftManagementService.GetOnSiteUserIdsForDayAsync"/>;
/// dietary data is read from <see cref="IUserServiceRead"/> (cached UserInfo —
/// dietary lives on Profile). Tests exercise the "unique humans across the week"
/// contract: a single human on-site multiple days contributes exactly once to
/// every aggregate, while still showing up in the correct per-day counts.
/// </summary>
public class CantinaRosterServiceTests
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IUserServiceRead _userRead;
    private readonly IClock _clock;
    private readonly CantinaRosterService _service;

    private static readonly LocalDate GateOpening = new(2026, 7, 7);
    private const string EventName = "Elsewhere 2026";
    private const int WeekStartOffset = 0;

    public CantinaRosterServiceTests()
    {
        _shiftMgmt = Substitute.For<IShiftManagementService>();
        _userRead = Substitute.For<IUserServiceRead>();
        // Fixed clock pinned to noon UTC on the gate-opening day; tests that
        // care about EventTodayDate semantics override on a per-test basis.
        _clock = new FakeClock(Instant.FromUtc(2026, 7, 7, 12, 0));

        // Default: no humans known (each test stubs its own via SetupHumans).
        _userRead.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));

        // Default: every day returns an empty on-site cohort.
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        _service = new CantinaRosterService(_shiftMgmt, _userRead, _clock);
    }

    /// <summary>Builds a Profile carrying burner name + dietary (dietary now lives on Profile).</summary>
    private static Profile Human(
        Guid userId,
        string burner,
        string? dietary = null,
        IReadOnlyList<string>? allergies = null,
        string? allergyOther = null,
        IReadOnlyList<string>? intolerances = null,
        string? intoleranceOther = null) => new()
        {
            UserId = userId,
            BurnerName = burner,
            DietaryPreference = dietary,
            Allergies = allergies is null ? [] : [.. allergies],
            AllergyOtherText = allergyOther,
            Intolerances = intolerances is null ? [] : [.. intolerances],
            IntoleranceOtherText = intoleranceOther,
        };

    private static EventSettings ActiveEvent() => new()
    {
        Id = Guid.NewGuid(),
        EventName = EventName,
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = GateOpening,
        IsActive = true
    };

    [HumansFact]
    public async Task GetWeeklyRoster_NoActiveEventSettings_ReturnsDtoWithNullDatesAndNoPeople()
    {
        _shiftMgmt.GetActiveAsync().Returns((EventSettings?)null);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.WeekStartOffset.Should().Be(WeekStartOffset);
        result.WeekStartDate.Should().BeNull();
        result.WeekEndDate.Should().BeNull();
        result.EventName.Should().BeNull();
        result.TotalUniqueOnSite.Should().Be(0);
        result.UnansweredCount.Should().Be(0);
        result.People.Should().BeEmpty();
        result.AllergyOtherEntries.Should().BeEmpty();
        result.IntoleranceOtherEntries.Should().BeEmpty();

        result.Days.Should().HaveCount(7);
        result.Days.Should().OnlyContain(d => d.CalendarDate == null && d.TotalOnSite == 0 && d.UnansweredOnDay == 0);
        result.Days.Select(d => d.DayOffset).Should().Equal(
            WeekStartOffset + 0, WeekStartOffset + 1, WeekStartOffset + 2,
            WeekStartOffset + 3, WeekStartOffset + 4, WeekStartOffset + 5,
            WeekStartOffset + 6);

        result.DietaryBreakdown.Should().ContainKey("Unanswered").WhoseValue.Should().Be(0);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            result.DietaryBreakdown.Should().ContainKey(pref).WhoseValue.Should().Be(0);

        result.AllergyRollup.Select(r => r.Label).Should().Equal(DietaryOptions.AllergyOptions);
        result.AllergyRollup.Should().OnlyContain(r => r.Count == 0);
        result.IntoleranceRollup.Select(r => r.Label).Should().Equal(DietaryOptions.IntoleranceOptions);
        result.IntoleranceRollup.Should().OnlyContain(r => r.Count == 0);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_NoOnSiteUsers_AnyDay_ReturnsZeroState()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.WeekStartDate.Should().Be(GateOpening);
        result.WeekEndDate.Should().Be(GateOpening.PlusDays(6));
        result.EventName.Should().Be(EventName);
        result.TotalUniqueOnSite.Should().Be(0);
        result.UnansweredCount.Should().Be(0);
        result.People.Should().BeEmpty();
        result.Days.Should().HaveCount(7);
        result.Days.Should().OnlyContain(d => d.TotalOnSite == 0 && d.UnansweredOnDay == 0);
        for (var i = 0; i < 7; i++)
            result.Days[i].CalendarDate.Should().Be(GateOpening.PlusDays(i));
        result.DietaryBreakdown.Values.Should().OnlyContain(v => v == 0);
        result.AllergyRollup.Should().OnlyContain(r => r.Count == 0);
        result.IntoleranceRollup.Should().OnlyContain(r => r.Count == 0);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_OneOmnivoreOnOneDay_AggregatesCorrectly()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var userId = Guid.NewGuid();

        // On-site Monday only (day 0 of the week).
        SetupDay(WeekStartOffset + 0, userId);
        SetupHumans(Human(userId, "AlicePrime", "Omnivore"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.TotalUniqueOnSite.Should().Be(1);
        result.UnansweredCount.Should().Be(0);
        result.DietaryBreakdown["Omnivore"].Should().Be(1);
        result.DietaryBreakdown["Unanswered"].Should().Be(0);

        result.Days.Should().HaveCount(7);
        result.Days[0].TotalOnSite.Should().Be(1);
        result.Days[0].UnansweredOnDay.Should().Be(0);
        for (var i = 1; i < 7; i++)
            result.Days[i].TotalOnSite.Should().Be(0);

        result.People.Should().HaveCount(1);
        var p = result.People[0];
        p.UserId.Should().Be(userId);
        p.BurnerName.Should().Be("AlicePrime");
        p.DietaryPreference.Should().Be("Omnivore");
        p.ArrivesOn.Should().Be(GateOpening);
        p.NoShift.Should().HaveCount(6);
        p.NoShift.Should().Equal(
            GateOpening.PlusDays(1),
            GateOpening.PlusDays(2),
            GateOpening.PlusDays(3),
            GateOpening.PlusDays(4),
            GateOpening.PlusDays(5),
            GateOpening.PlusDays(6));
    }

    [HumansFact]
    public async Task GetWeeklyRoster_OnePersonOnMultipleDays_CountedOnce()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var userId = Guid.NewGuid();

        // On-site Mon, Wed, Fri.
        SetupDay(WeekStartOffset + 0, userId);
        SetupDay(WeekStartOffset + 2, userId);
        SetupDay(WeekStartOffset + 4, userId);
        SetupHumans(Human(userId, "Alice", "Vegan"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.TotalUniqueOnSite.Should().Be(1);
        result.DietaryBreakdown["Vegan"].Should().Be(1);
        result.UnansweredCount.Should().Be(0);

        result.Days[0].TotalOnSite.Should().Be(1);
        result.Days[1].TotalOnSite.Should().Be(0);
        result.Days[2].TotalOnSite.Should().Be(1);
        result.Days[3].TotalOnSite.Should().Be(0);
        result.Days[4].TotalOnSite.Should().Be(1);
        result.Days[5].TotalOnSite.Should().Be(0);
        result.Days[6].TotalOnSite.Should().Be(0);

        result.People.Should().HaveCount(1);
        var p = result.People[0];
        p.ArrivesOn.Should().Be(GateOpening);
        p.NoShift.Should().HaveCount(4);
        p.NoShift.Should().Equal(
            GateOpening.PlusDays(1),
            GateOpening.PlusDays(3),
            GateOpening.PlusDays(5),
            GateOpening.PlusDays(6));
    }

    [HumansFact]
    public async Task GetWeeklyRoster_VolunteerWithoutDietary_CountsAsUnanswered_Once()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var userId = Guid.NewGuid();

        // On-site Mon, Tue, Wed — no dietary preference recorded.
        SetupDay(WeekStartOffset + 0, userId);
        SetupDay(WeekStartOffset + 1, userId);
        SetupDay(WeekStartOffset + 2, userId);
        SetupHumans(Human(userId, "BobBurner"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.TotalUniqueOnSite.Should().Be(1);
        result.UnansweredCount.Should().Be(1);
        result.DietaryBreakdown["Unanswered"].Should().Be(1);
        result.DietaryBreakdown.Values.Where(v => v > 0).Should().HaveCount(1);

        result.Days[0].UnansweredOnDay.Should().Be(1);
        result.Days[1].UnansweredOnDay.Should().Be(1);
        result.Days[2].UnansweredOnDay.Should().Be(1);

        result.People.Should().HaveCount(1);
        var p = result.People[0];
        p.BurnerName.Should().Be("BobBurner");
        p.DietaryPreference.Should().BeNull();
        p.Allergies.Should().BeEmpty();
        p.AllergyOtherText.Should().BeNull();
        p.Intolerances.Should().BeEmpty();
        p.IntoleranceOtherText.Should().BeNull();
        p.ArrivesOn.Should().Be(GateOpening);
        p.NoShift.Should().HaveCount(4);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_MixedCohort_RollsUpUniqueAcrossWeek()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        // A on-site Mon+Tue, B on Wed, C on Thu, D on Fri. D has no dietary.
        SetupDay(WeekStartOffset + 0, a);
        SetupDay(WeekStartOffset + 1, a);
        SetupDay(WeekStartOffset + 2, b);
        SetupDay(WeekStartOffset + 3, c);
        SetupDay(WeekStartOffset + 4, d);
        SetupHumans(
            Human(a, "Ava", "Vegetarian", allergies: ["Peanut", "Shellfish"], intolerances: ["Lactose"]),
            Human(b, "Beth", "Vegan", allergies: ["Peanut"]),
            Human(c, "Cleo", "Omnivore", allergies: ["Other"], allergyOther: "MSG"),
            Human(d, "Dee"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.TotalUniqueOnSite.Should().Be(4);
        result.UnansweredCount.Should().Be(1);
        result.DietaryBreakdown["Vegetarian"].Should().Be(1);
        result.DietaryBreakdown["Vegan"].Should().Be(1);
        result.DietaryBreakdown["Omnivore"].Should().Be(1);
        result.DietaryBreakdown["Pescatarian"].Should().Be(0);
        result.DietaryBreakdown["Unanswered"].Should().Be(1);

        var allergy = result.AllergyRollup.ToDictionary(r => r.Label, r => r.Count, StringComparer.Ordinal);
        allergy["Peanut"].Should().Be(2);
        allergy["Shellfish"].Should().Be(1);
        allergy["Other"].Should().Be(1);
        allergy["Tree nut"].Should().Be(0);
        allergy["Dairy"].Should().Be(0);
        allergy["Egg"].Should().Be(0);
        allergy["Wheat/Gluten"].Should().Be(0);
        allergy["Soy"].Should().Be(0);
        allergy["Sesame"].Should().Be(0);

        result.AllergyOtherEntries.Should().BeEquivalentTo(new[] { "MSG" });

        var intolerance = result.IntoleranceRollup.ToDictionary(r => r.Label, r => r.Count, StringComparer.Ordinal);
        intolerance["Lactose"].Should().Be(1);
        intolerance["Gluten"].Should().Be(0);
        intolerance["Histamine"].Should().Be(0);
        intolerance["FODMAP"].Should().Be(0);
        intolerance["Other"].Should().Be(0);
        result.IntoleranceOtherEntries.Should().BeEmpty();

        result.Days[0].TotalOnSite.Should().Be(1);
        result.Days[1].TotalOnSite.Should().Be(1);
        result.Days[2].TotalOnSite.Should().Be(1);
        result.Days[3].TotalOnSite.Should().Be(1);
        result.Days[4].TotalOnSite.Should().Be(1);
        result.Days[5].TotalOnSite.Should().Be(0);
        result.Days[6].TotalOnSite.Should().Be(0);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_OtherTextDeduplicatedAcrossWeek()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        SetupDay(WeekStartOffset + 0, a);
        SetupDay(WeekStartOffset + 2, b);
        SetupHumans(
            Human(a, "Ava", "Omnivore", allergies: ["Other"], allergyOther: "MSG"),
            // identical free-text — must dedup
            Human(b, "Beth", "Omnivore", allergies: ["Other"], allergyOther: "MSG"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.TotalUniqueOnSite.Should().Be(2);
        result.AllergyOtherEntries.Should().BeEquivalentTo(new[] { "MSG" });
        result.AllergyOtherEntries.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_MedicalConditionsNeverInDto()
    {
        // The cantina output DTO must not expose MedicalConditions, even though
        // medical now lives on the cached UserInfo/ProfileInfo. GDPR Art.9 boundary.
        typeof(Humans.Application.Services.Cantina.Dtos.RosterPersonDto)
            .GetProperty("MedicalConditions").Should().BeNull(
            "RosterPersonDto must not expose MedicalConditions — GDPR Art.9 boundary.");

        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var userId = Guid.NewGuid();
        SetupDay(WeekStartOffset + 0, userId);
        // Profile carries medical; the cantina DTO/JSON must still omit it.
        SetupHumans(new Profile
        {
            UserId = userId,
            BurnerName = "Sensitive",
            DietaryPreference = "Omnivore",
            MedicalConditions = "Severe peanut allergy",
        });

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("MedicalConditions");
        json.Should().NotContain("Severe peanut allergy");
    }

    // Display-sort tests live in Humans.Web.Tests/Cantina/CantinaRosterAssemblerTests.cs.

    [HumansFact]
    public async Task GetWeeklyRoster_ArrivesOn_IsEarliestOnSiteDay_AndNoShift_IsComplement()
    {
        // Single human on-site Mon + Wed + Sat (days 0, 2, 5).
        // Expected: ArrivesOn = Mon (earliest), NoShift = [Tue, Thu, Fri, Sun].
        // Also verifies the cohort-exclusion invariant: a known human with NO
        // signups all week does NOT appear in People.
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var onSiteUserId = Guid.NewGuid();
        var excludedUserId = Guid.NewGuid(); // never appears on any day → must be excluded

        SetupDay(WeekStartOffset + 0, onSiteUserId);
        SetupDay(WeekStartOffset + 2, onSiteUserId);
        SetupDay(WeekStartOffset + 5, onSiteUserId);
        SetupHumans(Human(onSiteUserId, "OnSite", "Omnivore"), Human(excludedUserId, "Excluded"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.People.Should().HaveCount(1);
        result.People.Should().NotContain(p => p.UserId == excludedUserId);

        var p = result.People[0];
        p.UserId.Should().Be(onSiteUserId);
        p.ArrivesOn.Should().Be(GateOpening); // Mon = week day 0
        p.NoShift.Should().HaveCount(4);
        p.NoShift.Should().Equal(
            GateOpening.PlusDays(1), // Tue
            GateOpening.PlusDays(3), // Thu
            GateOpening.PlusDays(4), // Fri
            GateOpening.PlusDays(6)); // Sun
    }

    // ---- helpers ----

    /// <summary>Stubs the on-site cohort for a single day (dietary comes from SetupHumans).</summary>
    private void SetupDay(int dayOffset, params Guid[] onSiteIds) =>
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(dayOffset, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(onSiteIds));

    /// <summary>
    /// Stubs <see cref="IUserServiceRead.GetUserInfosAsync"/> to return a
    /// <c>UserInfo</c> per profile. Dietary + burner name are read off the Profile.
    /// </summary>
    private void SetupHumans(params Profile[] profiles)
    {
        var dict = profiles.ToDictionary(
            p => p.UserId,
            p => UserInfo.Create(
                user: new User { Id = p.UserId, DisplayName = p.BurnerName, PreferredLanguage = "en" },
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: p,
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: []));
        _userRead.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict));
    }
}
