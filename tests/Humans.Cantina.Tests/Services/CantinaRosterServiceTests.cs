using System.Security.Claims;
using Humans.AuditLog.Contracts;
using Humans.Base.Csv;
using Humans.Cantina.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Cantina.Contracts;
using Humans.Cantina.Domain;
using Humans.Cantina.Services;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Cantina.Tests.Services;

/// <summary>
/// <see cref="CantinaRosterService"/> "unique humans across the week" contract: a single
/// human on-site multiple days contributes exactly once to every aggregate, while still
/// showing up in the correct per-day counts. The on-site cohort comes from
/// <see cref="IShiftService.GetOnSiteUserIdsForDayAsync"/>, names from
/// <see cref="IUserService"/>, dietary from <c>ICantinaService</c>.
/// </summary>
public class CantinaRosterServiceTests
{
    private readonly IShiftService _shiftMgmt;
    private readonly ISettingsService _burnSettings;
    private readonly IUserService _userRead;
    private readonly ICantinaService _dietary = Substitute.For<ICantinaService>();
    private readonly IClock _clock;
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly CantinaRosterService _service;

    private static readonly LocalDate GateOpening = new(2026, 7, 7);
    private const string EventName = "Elsewhere 2026";
    private const int WeekStartOffset = 0;

    public CantinaRosterServiceTests()
    {
        _shiftMgmt = Substitute.For<IShiftService>();
        _burnSettings = Substitute.For<ISettingsService>();
        _userRead = Substitute.For<IUserService>();
        // Fixed clock pinned to noon UTC on the gate-opening day; tests that
        // care about EventTodayDate semantics override on a per-test basis.
        _clock = new FakeClock(Instant.FromUtc(2026, 7, 7, 12, 0));

        _userRead.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));
        _dietary.GetDietaryProfilesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DietaryProfileInfo>());

        _shiftMgmt.GetOnSiteUserIdsForDayAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        _service = new CantinaRosterService(_shiftMgmt, _burnSettings, _userRead, _dietary, _clock, _audit);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task CsvDownloads_AuditActorAndPersonalRows_WithoutAuditingPageReads(bool daily, bool populated)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var burn = ActiveEvent();
        _burnSettings.GetActiveEventSettingsAsync(ct).Returns(burn);
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(burn.Id, Arg.Any<int>(), ct)
            .Returns(populated ? new[] { personId } : Array.Empty<Guid>());
        SetupHumans(Human(personId, "Chef", dietary: "Vegan", allergies: ["Other"], allergyOther: "Peanuts"));
        var controller = Controller(actorId);

        var result = daily ? await controller.DayCsv(0, ct) : await controller.Csv(0, ct);

        var file = result.Should().BeOfType<FileContentResult>().Which;
        if (populated)
            HumansCsv.Utf8WithBom.GetString(file.FileContents).Should().Contain("Chef").And.Contain("Peanuts");
        await _audit.Received(1).LogAsync(CantinaAuditActions.CantinaRosterExported, "EventSettings", burn.Id,
            Arg.Is<string>(text => text.StartsWith(daily ? "Daily" : "Weekly", StringComparison.Ordinal)
                && text.Contains($"{(populated ? 1 : 0)} people rows", StringComparison.Ordinal)), actorId);
        var page = daily ? await controller.Day(0, ct) : await controller.Roster(0, ct);
        page.Should().BeOfType<ViewResult>();
        _audit.ReceivedCalls().Should().ContainSingle("page reads do not disclose a file");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task HealthReveal_IsAudited_AndUnlocksOnlyThePageItWasPostedFor(bool daily)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var burn = ActiveEvent();
        _burnSettings.GetActiveEventSettingsAsync(ct).Returns(burn);
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(burn.Id, Arg.Any<int>(), ct).Returns(new[] { personId });
        SetupHumans(Human(personId, "Chef", allergies: ["Other"], allergyOther: "Peanuts"));
        var controller = Controller(actorId);

        var locked = daily ? await controller.Day(0, ct) : await controller.Roster(0, ct);
        locked.Should().BeOfType<ViewResult>().Which.ViewData[CantinaController.HealthRevealedKey].Should().Be(false);
        _audit.ReceivedCalls().Should().BeEmpty("the locked page discloses no health data");

        var reveal = daily ? await controller.RevealDayHealth(0, ct) : await controller.RevealHealth(0, ct);
        reveal.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(daily ? nameof(CantinaController.Day) : nameof(CantinaController.Roster));
        await _audit.Received(1).LogAsync(CantinaAuditActions.CantinaHealthDetailsRevealed, "EventSettings", burn.Id,
            Arg.Is<string>(text => text.Contains("allergy and intolerance details revealed", StringComparison.Ordinal)
                && text.Contains("1 people rows", StringComparison.Ordinal)), actorId);
        _audit.ReceivedCalls().Should().ContainSingle("a reveal is one row for the page, not one per member it shows");
        _audit.ClearReceivedCalls();

        var other = daily ? await controller.Day(1, ct) : await controller.Roster(1, ct);
        other.Should().BeOfType<ViewResult>().Which.ViewData[CantinaController.HealthRevealedKey].Should().Be(false, "a reveal is for one page only");

        var revealed = daily ? await controller.Day(0, ct) : await controller.Roster(0, ct);
        revealed.Should().BeOfType<ViewResult>().Which.ViewData[CantinaController.HealthRevealedKey].Should().Be(true);
        _audit.ReceivedCalls().Should().BeEmpty("the render after the reveal is covered by the reveal's own entries");
    }

    [HumansFact]
    public async Task HealthReveal_WritesOneRow_HoweverManyMembersItShows()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var people = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        var burn = ActiveEvent();
        _burnSettings.GetActiveEventSettingsAsync(ct).Returns(burn);
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(burn.Id, Arg.Any<int>(), ct).Returns(people);
        SetupHumans(people.Select((id, i) => Human(id, $"Cook {i}", allergies: ["Other"], allergyOther: "Peanuts")).ToArray());

        await _service.GetWeeklyRosterAsync(0, RosterDisclosure.HealthReveal(actorId), ct);
        _audit.ReceivedCalls().Should().ContainSingle();
        _audit.ClearReceivedCalls();

        await _service.GetDailyRosterAsync(0, RosterDisclosure.HealthReveal(actorId), ct);
        await _audit.Received(1).LogAsync(CantinaAuditActions.CantinaHealthDetailsRevealed, "EventSettings", burn.Id,
            Arg.Is<string>(text => text.Contains("200 people rows", StringComparison.Ordinal)
                && !text.Contains("Peanuts", StringComparison.Ordinal)), actorId);
        _audit.ReceivedCalls().Should().ContainSingle();
        _audit.ClearReceivedCalls();

        await _service.GetDailyRosterAsync(0, RosterDisclosure.Export(actorId), ct);
        _audit.ReceivedCalls().Should().ContainSingle("exports keep their one scope row");
    }

    private CantinaController Controller(Guid actorId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        return new CantinaController(_service, NullLogger<CantinaController>.Instance, _userRead)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>()),
        };
    }

    /// <summary>Builds a human: burner name on the profile, dietary as Cantina's own record.</summary>
    private static (Guid UserId, ProfileInfo Profile, DietaryProfileInfo? Diet) Human(
        Guid userId,
        string burner,
        string? dietary = null,
        IReadOnlyList<string>? allergies = null,
        string? allergyOther = null,
        IReadOnlyList<string>? intolerances = null,
        string? intoleranceOther = null) => (
        userId,
        UserFixtures.Profile(burnerName: burner),
        dietary is null && allergies is null && intolerances is null
            ? null
            : new DietaryProfileInfo(userId, dietary, allergies ?? [], allergyOther, intolerances ?? [], intoleranceOther));

    private static EventSettingsInfo ActiveEvent() => new(
        Id: Guid.NewGuid(),
        EventName: EventName,
        Year: GateOpening.Year,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: GateOpening,
        BuildStartOffset: 0,
        EventEndOffset: 0,
        StrikeEndOffset: 0,
        FirstCrewStartOffset: 0,
        SetupWeekStartOffset: 0,
        PreEventWeekStartOffset: 0,
        FinishingWeekendStartOffset: 0,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);

    /// <summary>
    /// Active event with an explicit build→strike offset range so the
    /// arrival-day scan (<c>BuildFirstConfirmedOffsetByUserAsync</c>) covers
    /// days outside the visible week. The default <see cref="ActiveEvent"/>
    /// leaves both offsets at 0, which would scan only day 0.
    /// </summary>
    private static EventSettingsInfo ActiveEventWithRange(int buildStart, int strikeEnd) =>
        ActiveEvent() with { BuildStartOffset = buildStart, StrikeEndOffset = strikeEnd };

    [HumansFact]
    public async Task GetWeeklyRoster_NoOffset_DefaultsToTheWeekContainingToday()
    {
        // Clock is Tue 7 Jul 2026 12:00 UTC — 14:00 in Madrid, same calendar
        // day — and GateOpening is Tue 7 Jul, so the Monday of this week is
        // 6 Jul: offset -1.
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(ActiveEvent());

        var result = await _service.GetWeeklyRosterAsync(ct: Xunit.TestContext.Current.CancellationToken);

        result.WeekStartOffset.Should().Be(-1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_NoOffsetAndNoActiveEvent_DefaultsToZero()
    {
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        var result = await _service.GetWeeklyRosterAsync(ct: Xunit.TestContext.Current.CancellationToken);

        result.WeekStartOffset.Should().Be(0);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_NoActiveEventSettings_ReturnsDtoWithNullDatesAndNoPeople()
    {
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

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
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

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
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();

        SetupDay(WeekStartOffset + 0, userId);
        SetupHumans(Human(userId, "AlicePrime", "Omnivore"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

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
        p.ArrivesOn.Should().Be(GateOpening.PlusDays(-1));
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
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();

        SetupDay(WeekStartOffset + 0, userId);
        SetupDay(WeekStartOffset + 2, userId);
        SetupDay(WeekStartOffset + 4, userId);
        SetupHumans(Human(userId, "Alice", "Vegan"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

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
        p.ArrivesOn.Should().Be(GateOpening.PlusDays(-1));
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
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();

        // No dietary preference recorded.
        SetupDay(WeekStartOffset + 0, userId);
        SetupDay(WeekStartOffset + 1, userId);
        SetupDay(WeekStartOffset + 2, userId);
        SetupHumans(Human(userId, "BobBurner"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

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
        p.ArrivesOn.Should().Be(GateOpening.PlusDays(-1));
        p.NoShift.Should().HaveCount(4);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_MixedCohort_RollsUpUniqueAcrossWeek()
    {
        var es = ActiveEvent();
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

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

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

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
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        SetupDay(WeekStartOffset + 0, a);
        SetupDay(WeekStartOffset + 2, b);
        SetupHumans(
            Human(a, "Ava", "Omnivore", allergies: ["Other"], allergyOther: "MSG"),
            // identical free-text — must dedup
            Human(b, "Beth", "Omnivore", allergies: ["Other"], allergyOther: "MSG"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

        result.TotalUniqueOnSite.Should().Be(2);
        result.AllergyOtherEntries.Should().BeEquivalentTo(new[] { "MSG" });
        result.AllergyOtherEntries.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_MedicalConditionsNeverInDto()
    {
        // The cantina output DTO must not expose MedicalConditions, even though
        // medical rides on the cached UserInfo (its Profile). GDPR Art.9 boundary.
        typeof(Humans.Cantina.Services.Dtos.RosterPersonDto)
            .GetProperty("MedicalConditions").Should().BeNull(
            "RosterPersonDto must not expose MedicalConditions — GDPR Art.9 boundary.");

        var es = ActiveEvent();
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();
        SetupDay(WeekStartOffset + 0, userId);
        // Profile carries medical; the cantina DTO/JSON must still omit it.
        SetupHumans((userId, UserFixtures.Profile(
            burnerName: "Sensitive",
            medicalConditions: "Severe peanut allergy"),
            new DietaryProfileInfo(userId, "Omnivore", [], null, [], null)));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("MedicalConditions");
        json.Should().NotContain("Severe peanut allergy");
    }

    [HumansFact]
    public async Task GetWeeklyRoster_ArrivesOn_IsTheDayBeforeTheFirstShift_AndNoShift_IsComplement()
    {
        // Single human on-site Mon + Wed + Sat (days 0, 2, 5).
        // Also verifies the cohort-exclusion invariant: a known human with NO
        // signups all week does NOT appear in People.
        var es = ActiveEvent();
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var onSiteUserId = Guid.NewGuid();
        var excludedUserId = Guid.NewGuid();

        SetupDay(WeekStartOffset + 0, onSiteUserId);
        SetupDay(WeekStartOffset + 2, onSiteUserId);
        SetupDay(WeekStartOffset + 5, onSiteUserId);
        SetupHumans(Human(onSiteUserId, "OnSite", "Omnivore"), Human(excludedUserId, "Excluded"));

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset, ct: Xunit.TestContext.Current.CancellationToken);

        result.People.Should().HaveCount(1);
        result.People.Should().NotContain(p => p.UserId == excludedUserId);

        var p = result.People[0];
        p.UserId.Should().Be(onSiteUserId);
        p.ArrivesOn.Should().Be(GateOpening.PlusDays(-1));
        p.NoShift.Should().HaveCount(4);
        p.NoShift.Should().Equal(
            GateOpening.PlusDays(1),
            GateOpening.PlusDays(3),
            GateOpening.PlusDays(4),
            GateOpening.PlusDays(6));
    }

    [HumansFact]
    public async Task GetWeeklyRoster_FeedsHumanDayBeforeFirstConfirmedShift()
    {
        // Human's only confirmed shift is Wednesday (offset 2). They have no
        // signup on any visible-week day other than via that shift, so the
        // roster must feed them the day before — Tuesday (offset 1) — as their
        // arrival day, pulling them into the cohort even though they were not
        // returned for any visible-week load except day 2.
        var ev = ActiveEventWithRange(buildStart: -2, strikeEnd: 8);
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var id = Guid.NewGuid();
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, 2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { id }));
        SetupHumans(Human(id, "Ash"));

        var result = await _service.GetWeeklyRosterAsync(0, ct: Xunit.TestContext.Current.CancellationToken);

        var person = result.People.Single(p => p.UserId == id);
        // Arrival = day before the first confirmed shift (offset 2 → offset 1).
        person.ArrivesOn.Should().Be(GateOpening.PlusDays(1));
        // NoShift is the complement of on-site days; the arrival day counts as on-site.
        person.NoShift.Should().NotContain(GateOpening.PlusDays(1));
        person.NoShift.Should().NotContain(GateOpening.PlusDays(2));
        // The weekly per-day strip counts the arrival person on Tuesday (offset 1),
        // matching what the daily drill-down will show.
        result.Days[1].TotalOnSite.Should().Be(1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_ArrivalOnlyHuman_PulledIntoWeek()
    {
        // Human's ONLY confirmed shift is offset 7 (next week's first day).
        // Viewing week 0 (offsets 0..6), their arrival = offset 6, which IS in
        // this week. They have NO shift in offsets 0..6 — the arrival day alone
        // pulls them into the cohort.
        var ev = ActiveEventWithRange(buildStart: -2, strikeEnd: 8);
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var id = Guid.NewGuid();
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, 7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { id }));
        SetupHumans(Human(id, "Bo"));

        var result = await _service.GetWeeklyRosterAsync(0, ct: Xunit.TestContext.Current.CancellationToken);

        result.People.Should().ContainSingle(p => p.UserId == id);
        var person = result.People.Single(p => p.UserId == id);
        person.ArrivesOn.Should().Be(GateOpening.PlusDays(6));
        result.TotalUniqueOnSite.Should().Be(1);
        result.Days[6].TotalOnSite.Should().Be(1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_ArrivalDayBeforeEvent_NotClamped()
    {
        // Human's first confirmed shift is offset 0 (gate opening). Arrival =
        // offset -1. Viewing the PREVIOUS week (offsets -7..-1); offset -1 is
        // the last day of that window (index 6). The pre-event/negative arrival
        // day must NOT be clamped — the human must appear with ArrivesOn at -1.
        var ev = ActiveEventWithRange(buildStart: -7, strikeEnd: 2);
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var id = Guid.NewGuid();
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { id }));
        SetupHumans(Human(id, "Cy"));

        var result = await _service.GetWeeklyRosterAsync(-7, ct: Xunit.TestContext.Current.CancellationToken);

        var person = result.People.Single(p => p.UserId == id);
        person.ArrivesOn.Should().Be(GateOpening.PlusDays(-1));
        result.Days[6].TotalOnSite.Should().Be(1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_FirstShiftInPriorWeek_ArrivesOnIsTheTrueArrival_AndNoArrivalFedThisWeek()
    {
        // Confirmed shifts on offset 2 (a prior week) and offset 8 (the viewed week, offsets 7..13). The arrival is
        // offset 1, outside the viewed week: ArrivesOn still reports it, but no arrival day is fed this week.
        var ev = ActiveEventWithRange(buildStart: -2, strikeEnd: 14);
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var id = Guid.NewGuid();
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, 2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { id }));
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, 8, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { id }));
        SetupHumans(Human(id, "Di"));

        var result = await _service.GetWeeklyRosterAsync(7, ct: Xunit.TestContext.Current.CancellationToken);

        var person = result.People.Single(p => p.UserId == id);
        person.ArrivesOn.Should().Be(GateOpening.PlusDays(1));
        result.Days[0].TotalOnSite.Should().Be(0);
    }

    /// <summary>Stubs the on-site cohort for a single day (dietary comes from SetupHumans).</summary>
    private void SetupDay(int dayOffset, params Guid[] onSiteIds) =>
        _shiftMgmt.GetOnSiteUserIdsForDayAsync(
                Arg.Any<Guid>(), dayOffset, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(onSiteIds));

    /// <summary>
    /// Stubs <see cref="IUserService.GetUserInfosAsync"/> to return a
    /// <c>UserInfo</c> per profile; names only, dietary is stubbed on <c>ICantinaService</c>.
    /// </summary>
    private void SetupHumans(params (Guid UserId, ProfileInfo Profile, DietaryProfileInfo? Diet)[] profiles)
    {
        var dict = profiles.ToDictionary(
            p => p.UserId,
            p => UserInfo.Create(
                // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
                user: new User { Id = p.UserId, DisplayName = p.Profile.BurnerName, BurnerName = p.Profile.BurnerName, PreferredLanguage = "en" },
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: p.Profile,
                communicationPreferences: []));
        _userRead.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict));
        IReadOnlyDictionary<Guid, DietaryProfileInfo> diets = profiles
            .Where(p => p.Diet is not null)
            .ToDictionary(p => p.UserId, p => p.Diet!);
        _dietary.GetDietaryProfilesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(diets);
    }
}
