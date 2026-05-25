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
/// Unit tests for <see cref="CantinaRosterService.GetDailyRosterAsync"/> —
/// the per-day matrix payload that powers the drill-down view linked from
/// each row of the Cantina Weekly Roster's per-day mini-table (feature #36).
/// Companion to <see cref="CantinaRosterServiceTests"/>. On-site cohort comes
/// from the shift service; dietary from the cached UserInfo (Profile).
/// </summary>
public class CantinaDailyRosterServiceTests
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IUserServiceRead _userRead;
    private readonly IClock _clock;
    private readonly CantinaRosterService _service;

    private static readonly LocalDate GateOpening = new(2026, 7, 7); // a Tuesday
    private const string EventName = "Elsewhere 2026";

    public CantinaDailyRosterServiceTests()
    {
        _shiftMgmt = Substitute.For<IShiftManagementService>();
        _userRead = Substitute.For<IUserServiceRead>();
        _clock = new FakeClock(Instant.FromUtc(2026, 7, 7, 12, 0));

        _userRead.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));

        _shiftMgmt.GetOnSiteUserIdsForDayAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        _service = new CantinaRosterService(_shiftMgmt, _userRead, _clock);
    }

    /// <summary>Builds a Profile carrying burner name + dietary (dietary lives on Profile).</summary>
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
    public async Task GetDailyRoster_NoActiveEventSettings_ReturnsEmpty()
    {
        _shiftMgmt.GetActiveAsync().Returns((EventSettings?)null);

        var result = await _service.GetDailyRosterAsync(dayOffset: 3);

        result.DayOffset.Should().Be(3);
        result.CalendarDate.Should().BeNull();
        result.EventTodayDate.Should().BeNull();
        result.EventName.Should().BeNull();
        result.WeekStartOffset.Should().Be(0);
        result.TotalOnSite.Should().Be(0);
        result.UnansweredCount.Should().Be(0);
        result.People.Should().BeEmpty();
        result.AllergyOtherEntries.Should().BeEmpty();
        result.IntoleranceOtherEntries.Should().BeEmpty();

        result.DietaryBreakdown.Should().ContainKey("Unanswered").WhoseValue.Should().Be(0);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            result.DietaryBreakdown.Should().ContainKey(pref).WhoseValue.Should().Be(0);
        result.AllergyRollup.Select(r => r.Label).Should().Equal(DietaryOptions.AllergyOptions);
        result.AllergyRollup.Should().OnlyContain(r => r.Count == 0);
        result.IntoleranceRollup.Select(r => r.Label).Should().Equal(DietaryOptions.IntoleranceOptions);
        result.IntoleranceRollup.Should().OnlyContain(r => r.Count == 0);
    }

    [HumansFact]
    public async Task GetDailyRoster_NoOnSiteUsers_ReturnsZeroState()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var result = await _service.GetDailyRosterAsync(dayOffset: 0);

        result.CalendarDate.Should().Be(GateOpening);
        result.EventName.Should().Be(EventName);
        result.TotalOnSite.Should().Be(0);
        result.UnansweredCount.Should().Be(0);
        result.People.Should().BeEmpty();
        result.DietaryBreakdown.Values.Should().OnlyContain(v => v == 0);
        result.AllergyRollup.Should().OnlyContain(r => r.Count == 0);
        result.IntoleranceRollup.Should().OnlyContain(r => r.Count == 0);
    }

    [HumansFact]
    public async Task GetDailyRoster_PopulatedDay_BuildsAggregatesAndPeople()
    {
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        // 3-user fixture mixing dietary, allergy, intolerance and "Other"-with-text combinations.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        // c intentionally has no dietary preference — must count as Unanswered.
        SetupDay(0, a, b, c);
        SetupHumans(
            Human(a, "Ava", "Vegan", allergies: ["Peanut", "Other"], allergyOther: "MSG", intolerances: ["Lactose"]),
            Human(b, "Beth", "Omnivore", allergies: ["Peanut"]),
            Human(c, "Cleo"));

        var result = await _service.GetDailyRosterAsync(dayOffset: 0);

        result.TotalOnSite.Should().Be(3);
        result.UnansweredCount.Should().Be(1);

        result.DietaryBreakdown["Vegan"].Should().Be(1);
        result.DietaryBreakdown["Omnivore"].Should().Be(1);
        result.DietaryBreakdown["Vegetarian"].Should().Be(0);
        result.DietaryBreakdown["Pescatarian"].Should().Be(0);
        result.DietaryBreakdown["Unanswered"].Should().Be(1);

        var allergy = result.AllergyRollup.ToDictionary(r => r.Label, r => r.Count, StringComparer.Ordinal);
        allergy["Peanut"].Should().Be(2);
        allergy["Other"].Should().Be(1);
        allergy["Tree nut"].Should().Be(0);
        result.AllergyOtherEntries.Should().Equal(new[] { "MSG" });

        var intolerance = result.IntoleranceRollup.ToDictionary(r => r.Label, r => r.Count, StringComparer.Ordinal);
        intolerance["Lactose"].Should().Be(1);
        intolerance["Gluten"].Should().Be(0);
        result.IntoleranceOtherEntries.Should().BeEmpty();

        result.People.Should().HaveCount(3);
        var rowA = result.People.Single(p => p.UserId == a);
        rowA.BurnerName.Should().Be("Ava");
        rowA.DietaryPreference.Should().Be("Vegan");
        // Allergies must be an O(1) set so the matrix view can hit-test cells.
        rowA.Allergies.Should().BeAssignableTo<IReadOnlySet<string>>();
        rowA.Allergies.Should().BeEquivalentTo(new[] { "Peanut", "Other" });
        rowA.AllergyOtherText.Should().Be("MSG");
        rowA.Intolerances.Should().BeEquivalentTo(new[] { "Lactose" });

        var rowC = result.People.Single(p => p.UserId == c);
        rowC.BurnerName.Should().Be("Cleo");
        rowC.DietaryPreference.Should().BeNull();
        rowC.Allergies.Should().BeEmpty();
        rowC.Intolerances.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDailyRoster_MedicalConditionsNeverInDto()
    {
        // The cantina output DTO must not expose MedicalConditions, even though
        // medical now lives on the cached UserInfo/ProfileInfo. GDPR Art.9 boundary.
        typeof(Humans.Application.Services.Cantina.Dtos.DailyPersonRowDto)
            .GetProperty("MedicalConditions").Should().BeNull(
            "DailyPersonRowDto must not expose MedicalConditions — GDPR Art.9 boundary.");

        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var userId = Guid.NewGuid();
        SetupDay(0, userId);
        SetupHumans(new Profile
        {
            UserId = userId,
            BurnerName = "Sensitive",
            DietaryPreference = "Omnivore",
            MedicalConditions = "Severe peanut allergy",
        });

        var result = await _service.GetDailyRosterAsync(dayOffset: 0);

        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("MedicalConditions");
        json.Should().NotContain("Severe peanut allergy");
    }

    [HumansFact]
    public async Task GetDailyRoster_WeekStartOffset_ComputedCorrectly()
    {
        // GateOpening = Tue 7 Jul 2026. So:
        //   day 0  = Tue 7 Jul   → Monday of week = Mon 6 Jul → offset -1
        //   day -3 = Sat 4 Jul   → Monday of week = Mon 29 Jun → offset -8
        //   day +5 = Sun 12 Jul  → Monday of week = Mon 6 Jul → offset -1
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        (await _service.GetDailyRosterAsync(0)).WeekStartOffset.Should().Be(-1);
        (await _service.GetDailyRosterAsync(-3)).WeekStartOffset.Should().Be(-8);
        (await _service.GetDailyRosterAsync(5)).WeekStartOffset.Should().Be(-1);
    }

    [HumansFact]
    public async Task GetDailyRoster_PeopleNotSorted_ReturnsInRepoOrder()
    {
        // Service must NOT sort People — that's the Web-layer assembler's job
        // (memory/architecture/display-sort-in-controllers.md). Hand the service a
        // reverse-alphabetical order; the result must preserve it.
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var c = Guid.NewGuid();
        var b = Guid.NewGuid();
        var a = Guid.NewGuid();

        // Order on the wire: C, B, A. Service must not re-sort.
        SetupDay(0, c, b, a);
        SetupHumans(Human(c, "Charlie"), Human(b, "Bob"), Human(a, "Alice"));

        var result = await _service.GetDailyRosterAsync(dayOffset: 0);

        result.People.Select(p => p.BurnerName).Should().Equal("Charlie", "Bob", "Alice");
    }

    [HumansFact]
    public async Task GetDailyRoster_EventTodayDate_ComesFromEventTimezone()
    {
        // Sanity check: EventTodayDate is populated from the event timezone,
        // not from UTC. With clock pinned to 23:30 UTC on day 0, Europe/Madrid
        // is already day 1 (CET = UTC+2 in July).
        var fakeClock = new FakeClock(Instant.FromUtc(2026, 7, 7, 23, 30));
        var service = new CantinaRosterService(_shiftMgmt, _userRead, fakeClock);
        var es = ActiveEvent();
        _shiftMgmt.GetActiveAsync().Returns(es);

        var result = await service.GetDailyRosterAsync(dayOffset: 0);

        // CET = UTC+2 in July (DST). 23:30 UTC = 01:30 next day Madrid time.
        result.EventTodayDate.Should().Be(GateOpening.PlusDays(1));
    }

    // ---- helpers ----

    private void SetupDay(int dayOffset, params Guid[] onSiteIds) =>
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(dayOffset, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(onSiteIds));

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
