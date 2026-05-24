using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Cantina;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Cantina;

/// <summary>
/// Unit tests for <see cref="CantinaRosterService"/>. All dependencies
/// (<see cref="IShiftManagementRepository"/>, <see cref="IProfileService"/>,
/// <see cref="IUserService"/>) are substituted; the tests drive the
/// weekly stitching/aggregation logic directly. Tests exercise the
/// "unique humans across the week" contract end-to-end: a single human
/// on-site multiple days contributes exactly once to every aggregate,
/// while still showing up in the correct per-day counts.
/// </summary>
public class CantinaRosterServiceTests
{
    private readonly IShiftManagementRepository _shiftRepo;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly CantinaRosterService _service;

    private static readonly LocalDate GateOpening = new(2026, 7, 7);
    private const string EventName = "Elsewhere 2026";
    private const int WeekStartOffset = 0;

    public CantinaRosterServiceTests()
    {
        _shiftRepo = Substitute.For<IShiftManagementRepository>();
        _profileService = Substitute.For<IProfileService>();
        _userService = Substitute.For<IUserService>();

        // Sensible defaults — most tests override these as needed.
        _profileService.GetByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, Profile>>(
                new Dictionary<Guid, Profile>()));
        _userService.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                new Dictionary<Guid, User>()));

        // Default repo behaviour: every day in the week returns empty.
        // Individual tests override per-day with `SetupDay`.
        _shiftRepo.GetOnSiteUserIdsForDayAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));
        _shiftRepo.GetOnSiteVolunteerProfilesForDayAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VolunteerEventProfile>>(Array.Empty<VolunteerEventProfile>()));

        _service = new CantinaRosterService(_shiftRepo, _profileService, _userService);
    }

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
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);

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

        // Days has 7 entries, all with null CalendarDate and zero counts.
        result.Days.Should().HaveCount(7);
        result.Days.Should().OnlyContain(d => d.CalendarDate == null && d.TotalOnSite == 0 && d.UnansweredOnDay == 0);
        result.Days.Select(d => d.DayOffset).Should().Equal(
            WeekStartOffset + 0, WeekStartOffset + 1, WeekStartOffset + 2,
            WeekStartOffset + 3, WeekStartOffset + 4, WeekStartOffset + 5,
            WeekStartOffset + 6);

        // All canonical dietary buckets present, all at zero.
        result.DietaryBreakdown.Should().ContainKey("Unanswered").WhoseValue.Should().Be(0);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            result.DietaryBreakdown.Should().ContainKey(pref).WhoseValue.Should().Be(0);

        // All canonical allergy/intolerance chip labels present in rollup at zero.
        result.AllergyRollup.Select(r => r.Label).Should().Equal(DietaryOptions.AllergyOptions);
        result.AllergyRollup.Should().OnlyContain(r => r.Count == 0);
        result.IntoleranceRollup.Select(r => r.Label).Should().Equal(DietaryOptions.IntoleranceOptions);
        result.IntoleranceRollup.Should().OnlyContain(r => r.Count == 0);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_NoOnSiteUsers_AnyDay_ReturnsZeroState()
    {
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);
        // All 7 days return empty (default repo behaviour, set in ctor).

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.WeekStartDate.Should().Be(GateOpening);
        result.WeekEndDate.Should().Be(GateOpening.PlusDays(6));
        result.EventName.Should().Be(EventName);
        result.TotalUniqueOnSite.Should().Be(0);
        result.UnansweredCount.Should().Be(0);
        result.People.Should().BeEmpty();
        result.Days.Should().HaveCount(7);
        result.Days.Should().OnlyContain(d => d.TotalOnSite == 0 && d.UnansweredOnDay == 0);
        // Calendar dates present and consecutive Mon..Sun.
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
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Alice" };
        var profile = new Profile { UserId = userId, BurnerName = "AlicePrime" };
        var vep = new VolunteerEventProfile
        {
            UserId = userId,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        // On-site Monday only (day 0 of the week).
        SetupDay(WeekStartOffset + 0, new[] { userId }, new[] { vep });
        SetupUsers(user);
        SetupProfiles(profile);

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
        // DaysOff is the complement of on-site days within the 7-day week:
        // user is only on Mon, so Tue..Sun (6 days) are off.
        p.DaysOff.Should().HaveCount(6);
        p.DaysOff.Should().Equal(
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
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Alice" };
        var profile = new Profile { UserId = userId, BurnerName = "Alice" };
        var vep = new VolunteerEventProfile
        {
            UserId = userId,
            DietaryPreference = "Vegan",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        // On-site Mon, Wed, Fri.
        SetupDay(WeekStartOffset + 0, new[] { userId }, new[] { vep });
        SetupDay(WeekStartOffset + 2, new[] { userId }, new[] { vep });
        SetupDay(WeekStartOffset + 4, new[] { userId }, new[] { vep });
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        // Unique-across-week: counted once.
        result.TotalUniqueOnSite.Should().Be(1);
        result.DietaryBreakdown["Vegan"].Should().Be(1);
        result.UnansweredCount.Should().Be(0);

        // Per-day counts reflect presence on each day individually.
        result.Days[0].TotalOnSite.Should().Be(1);
        result.Days[1].TotalOnSite.Should().Be(0);
        result.Days[2].TotalOnSite.Should().Be(1);
        result.Days[3].TotalOnSite.Should().Be(0);
        result.Days[4].TotalOnSite.Should().Be(1);
        result.Days[5].TotalOnSite.Should().Be(0);
        result.Days[6].TotalOnSite.Should().Be(0);

        // Person arrived Mon (earliest on-site) and is off Tue/Thu/Sat/Sun
        // (the 4 days of the week with no signup).
        result.People.Should().HaveCount(1);
        var p = result.People[0];
        p.ArrivesOn.Should().Be(GateOpening);
        p.DaysOff.Should().HaveCount(4);
        p.DaysOff.Should().Equal(
            GateOpening.PlusDays(1),
            GateOpening.PlusDays(3),
            GateOpening.PlusDays(5),
            GateOpening.PlusDays(6));
    }

    [HumansFact]
    public async Task GetWeeklyRoster_VolunteerWithoutVEP_CountsAsUnanswered_Once()
    {
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Bob" };
        var profile = new Profile { UserId = userId, BurnerName = "BobBurner" };

        // On-site Mon, Tue, Wed — no VEP at all.
        SetupDay(WeekStartOffset + 0, new[] { userId }, Array.Empty<VolunteerEventProfile>());
        SetupDay(WeekStartOffset + 1, new[] { userId }, Array.Empty<VolunteerEventProfile>());
        SetupDay(WeekStartOffset + 2, new[] { userId }, Array.Empty<VolunteerEventProfile>());
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        // Unanswered counted once, not three times.
        result.TotalUniqueOnSite.Should().Be(1);
        result.UnansweredCount.Should().Be(1);
        result.DietaryBreakdown["Unanswered"].Should().Be(1);
        result.DietaryBreakdown.Values.Where(v => v > 0).Should().HaveCount(1);

        // Per-day unanswered counts still reflect each day individually.
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
        // On-site Mon/Tue/Wed → arrives Mon, off Thu/Fri/Sat/Sun.
        p.ArrivesOn.Should().Be(GateOpening);
        p.DaysOff.Should().HaveCount(4);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_MixedCohort_RollsUpUniqueAcrossWeek()
    {
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        var users = new[]
        {
            new User { Id = a, DisplayName = "A" },
            new User { Id = b, DisplayName = "B" },
            new User { Id = c, DisplayName = "C" },
            new User { Id = d, DisplayName = "D" }
        };
        var profiles = new[]
        {
            new Profile { UserId = a, BurnerName = "Ava" },
            new Profile { UserId = b, BurnerName = "Beth" },
            new Profile { UserId = c, BurnerName = "Cleo" },
            new Profile { UserId = d, BurnerName = "Dee" }
        };

        var vepA = new VolunteerEventProfile
        {
            UserId = a,
            DietaryPreference = "Vegetarian",
            Allergies = new List<string> { "Peanut", "Shellfish" },
            Intolerances = new List<string> { "Lactose" }
        };
        var vepB = new VolunteerEventProfile
        {
            UserId = b,
            DietaryPreference = "Vegan",
            Allergies = new List<string> { "Peanut" },
            Intolerances = new List<string>()
        };
        var vepC = new VolunteerEventProfile
        {
            UserId = c,
            DietaryPreference = "Omnivore",
            Allergies = new List<string> { "Other" },
            AllergyOtherText = "MSG",
            Intolerances = new List<string>()
        };
        // user d intentionally has no VEP at all.

        // A on-site Mon+Tue, B on Wed, C on Thu, D on Fri.
        SetupDay(WeekStartOffset + 0, new[] { a }, new[] { vepA });
        SetupDay(WeekStartOffset + 1, new[] { a }, new[] { vepA });
        SetupDay(WeekStartOffset + 2, new[] { b }, new[] { vepB });
        SetupDay(WeekStartOffset + 3, new[] { c }, new[] { vepC });
        SetupDay(WeekStartOffset + 4, new[] { d }, Array.Empty<VolunteerEventProfile>());
        SetupUsers(users);
        SetupProfiles(profiles);

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

        // Per-day distribution.
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
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var users = new[]
        {
            new User { Id = a, DisplayName = "A" },
            new User { Id = b, DisplayName = "B" }
        };
        var profiles = new[]
        {
            new Profile { UserId = a, BurnerName = "Ava" },
            new Profile { UserId = b, BurnerName = "Beth" }
        };
        var vepA = new VolunteerEventProfile
        {
            UserId = a,
            DietaryPreference = "Omnivore",
            Allergies = new List<string> { "Other" },
            AllergyOtherText = "MSG",
            Intolerances = new List<string>()
        };
        var vepB = new VolunteerEventProfile
        {
            UserId = b,
            DietaryPreference = "Omnivore",
            Allergies = new List<string> { "Other" },
            AllergyOtherText = "MSG", // identical free-text — must dedup
            Intolerances = new List<string>()
        };

        SetupDay(WeekStartOffset + 0, new[] { a }, new[] { vepA });
        SetupDay(WeekStartOffset + 2, new[] { b }, new[] { vepB });
        SetupUsers(users);
        SetupProfiles(profiles);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.TotalUniqueOnSite.Should().Be(2);
        result.AllergyOtherEntries.Should().BeEquivalentTo(new[] { "MSG" });
        result.AllergyOtherEntries.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetWeeklyRoster_MedicalConditionsNeverInDto()
    {
        // Compile-time guarantee — verify RosterPersonDto has no MedicalConditions property.
        var hasMedicalProp = typeof(Humans.Application.Services.Cantina.Dtos.RosterPersonDto)
            .GetProperty("MedicalConditions");
        hasMedicalProp.Should().BeNull(
            "RosterPersonDto must not expose MedicalConditions — GDPR Art.9 boundary.");

        // Runtime check: even with a populated MedicalConditions on the VEP,
        // the serialized DTO must not contain the medical-condition text.
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Sensitive" };
        var profile = new Profile { UserId = userId, BurnerName = "Sensitive" };
        var vep = new VolunteerEventProfile
        {
            UserId = userId,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>(),
            MedicalConditions = "Severe peanut allergy"
        };

        SetupDay(WeekStartOffset + 0, new[] { userId }, new[] { vep });
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("Severe peanut allergy");
        json.Should().NotContain("MedicalConditions");
    }

    [HumansFact]
    public async Task GetWeeklyRoster_People_OrderedByFirstArrivalThenAllergiesThenDietaryThenName()
    {
        // Coordinator-friendly sort priorities (see CantinaRosterService comment):
        //   1. First arrival date asc
        //   2. Has any allergies / intolerances desc (true first)
        //   3. Dietary in canonical order (Omnivore, Vegetarian, Vegan, Pescatarian, then unanswered last)
        //   4. BurnerName ordinal asc
        //
        // Fixture (week day 0 = Tue Jul 7 2026, day 1 = Wed Jul 8 2026):
        //   Alice   — day 0 (Tue), Vegan,      no allergies
        //   Bob     — day 0 (Tue), Omnivore,   Peanut allergy
        //   Charlie — day 1 (Wed), Omnivore,   no allergies
        //   Donna   — day 1 (Wed), Vegetarian, no allergies
        //
        // Expected order:
        //   Bob     (day 0, has allergy → first within day 0)
        //   Alice   (day 0, Vegan — only one non-allergy person on day 0)
        //   Charlie (day 1, Omnivore — first in canonical dietary order)
        //   Donna   (day 1, Vegetarian — second in canonical dietary order)
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var charlie = Guid.NewGuid();
        var donna = Guid.NewGuid();

        var users = new[]
        {
            new User { Id = alice, DisplayName = "Alice" },
            new User { Id = bob, DisplayName = "Bob" },
            new User { Id = charlie, DisplayName = "Charlie" },
            new User { Id = donna, DisplayName = "Donna" }
        };
        var profiles = new[]
        {
            new Profile { UserId = alice, BurnerName = "Alice" },
            new Profile { UserId = bob, BurnerName = "Bob" },
            new Profile { UserId = charlie, BurnerName = "Charlie" },
            new Profile { UserId = donna, BurnerName = "Donna" }
        };

        var vepAlice = new VolunteerEventProfile
        {
            UserId = alice,
            DietaryPreference = "Vegan",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };
        var vepBob = new VolunteerEventProfile
        {
            UserId = bob,
            DietaryPreference = "Omnivore",
            Allergies = new List<string> { "Peanut" },
            Intolerances = new List<string>()
        };
        var vepCharlie = new VolunteerEventProfile
        {
            UserId = charlie,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };
        var vepDonna = new VolunteerEventProfile
        {
            UserId = donna,
            DietaryPreference = "Vegetarian",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        // Day 0: Alice + Bob; Day 1: Charlie + Donna. Scrambled per-day order
        // to prove sort is service-side.
        SetupDay(WeekStartOffset + 0, new[] { bob, alice }, new[] { vepAlice, vepBob });
        SetupDay(WeekStartOffset + 1, new[] { donna, charlie }, new[] { vepCharlie, vepDonna });
        SetupUsers(users);
        SetupProfiles(profiles);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.People.Select(p => p.BurnerName).Should().Equal("Bob", "Alice", "Charlie", "Donna");
    }

    [HumansFact]
    public async Task GetWeeklyRoster_People_NameTiebreakWhenArrivalAllergyAndDietaryMatch()
    {
        // Two humans, same arrival day (0), same dietary (Omnivore), no allergies.
        // BurnerName ordinal asc should be the final tiebreaker.
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var zane = Guid.NewGuid();
        var anne = Guid.NewGuid();
        var users = new[]
        {
            new User { Id = zane, DisplayName = "Zane" },
            new User { Id = anne, DisplayName = "Anne" }
        };
        var profiles = new[]
        {
            new Profile { UserId = zane, BurnerName = "Zane" },
            new Profile { UserId = anne, BurnerName = "Anne" }
        };
        var vepZ = new VolunteerEventProfile
        {
            UserId = zane,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };
        var vepA = new VolunteerEventProfile
        {
            UserId = anne,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        SetupDay(WeekStartOffset + 0, new[] { zane, anne }, new[] { vepZ, vepA });
        SetupUsers(users);
        SetupProfiles(profiles);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.People.Select(p => p.BurnerName).Should().Equal("Anne", "Zane");
    }

    [HumansFact]
    public async Task GetWeeklyRoster_People_UnansweredDietarySortsAfterAnswered()
    {
        // Two humans, same arrival day (0), same allergy status (none).
        // The one with a known dietary (Omnivore) sorts before the unanswered one
        // even when the unanswered one's BurnerName is alphabetically earlier.
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var aaron = Guid.NewGuid();   // Unanswered (no VEP), BurnerName "Aaron"
        var bertha = Guid.NewGuid();  // Omnivore, BurnerName "Bertha"
        var users = new[]
        {
            new User { Id = aaron, DisplayName = "Aaron" },
            new User { Id = bertha, DisplayName = "Bertha" }
        };
        var profiles = new[]
        {
            new Profile { UserId = aaron, BurnerName = "Aaron" },
            new Profile { UserId = bertha, BurnerName = "Bertha" }
        };
        var vepBertha = new VolunteerEventProfile
        {
            UserId = bertha,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        SetupDay(WeekStartOffset + 0, new[] { aaron, bertha }, new[] { vepBertha });
        SetupUsers(users);
        SetupProfiles(profiles);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        result.People.Select(p => p.BurnerName).Should().Equal("Bertha", "Aaron");
    }

    [HumansFact]
    public async Task GetWeeklyRoster_ArrivesOn_IsEarliestOnSiteDay_AndDaysOff_IsComplement()
    {
        // Single human on-site Mon + Wed + Sat (days 0, 2, 5).
        // Expected: ArrivesOn = Mon (earliest), DaysOff = [Tue, Thu, Fri, Sun].
        // Also verifies the cohort-exclusion invariant: a second user with NO
        // signups all week does NOT appear in People (the "all week off" rule).
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);

        var onSiteUserId = Guid.NewGuid();
        var excludedUserId = Guid.NewGuid(); // never appears on any day → must be excluded
        var onSiteUser = new User { Id = onSiteUserId, DisplayName = "OnSite" };
        var excludedUser = new User { Id = excludedUserId, DisplayName = "Excluded" };
        var onSiteProfile = new Profile { UserId = onSiteUserId, BurnerName = "OnSite" };
        var excludedProfile = new Profile { UserId = excludedUserId, BurnerName = "Excluded" };
        var vep = new VolunteerEventProfile
        {
            UserId = onSiteUserId,
            DietaryPreference = "Omnivore",
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        SetupDay(WeekStartOffset + 0, new[] { onSiteUserId }, new[] { vep });
        SetupDay(WeekStartOffset + 2, new[] { onSiteUserId }, new[] { vep });
        SetupDay(WeekStartOffset + 5, new[] { onSiteUserId }, new[] { vep });
        SetupUsers(onSiteUser, excludedUser);
        SetupProfiles(onSiteProfile, excludedProfile);

        var result = await _service.GetWeeklyRosterAsync(WeekStartOffset);

        // Cohort-exclusion: excludedUserId never had a signup → not in People.
        result.People.Should().HaveCount(1);
        result.People.Should().NotContain(p => p.UserId == excludedUserId);

        var p = result.People[0];
        p.UserId.Should().Be(onSiteUserId);
        p.ArrivesOn.Should().Be(GateOpening); // Mon = week day 0
        p.DaysOff.Should().HaveCount(4);
        p.DaysOff.Should().Equal(
            GateOpening.PlusDays(1), // Tue
            GateOpening.PlusDays(3), // Thu
            GateOpening.PlusDays(4), // Fri
            GateOpening.PlusDays(6)); // Sun
    }

    // ---- helpers ----

    /// <summary>
    /// Sets up the repo mocks for a single day in the week. Days not
    /// configured fall back to the default (empty) behaviour set in the ctor.
    /// </summary>
    private void SetupDay(int dayOffset, IReadOnlyList<Guid> onSiteIds, IReadOnlyList<VolunteerEventProfile> veps)
    {
        _shiftRepo.GetOnSiteUserIdsForDayAsync(dayOffset, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(onSiteIds));
        _shiftRepo.GetOnSiteVolunteerProfilesForDayAsync(dayOffset, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(veps));
    }

    private void SetupUsers(params User[] users)
    {
        var dict = users.ToDictionary(u => u.Id);
        _userService.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, User>>(dict));
    }

    private void SetupProfiles(params Profile[] profiles)
    {
        var dict = profiles.ToDictionary(p => p.UserId);
        _profileService.GetByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, Profile>>(dict));
    }
}
