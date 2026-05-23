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
/// stitching/aggregation logic directly.
/// </summary>
public class CantinaRosterServiceTests
{
    private readonly IShiftManagementRepository _shiftRepo;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly CantinaRosterService _service;

    private static readonly LocalDate GateOpening = new(2026, 7, 7);
    private const string EventName = "Elsewhere 2026";

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
    public async Task GetDailyRoster_NoActiveEventSettings_ReturnsDtoWithNullCalendarAndNoPeople()
    {
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);
        _shiftRepo.GetOnSiteUserIdsForDayAsync(0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        var result = await _service.GetDailyRosterAsync(0);

        result.CalendarDate.Should().BeNull();
        result.EventName.Should().BeNull();
        result.TotalOnSite.Should().Be(0);
        result.UnansweredCount.Should().Be(0);
        result.People.Should().BeEmpty();
        result.AllergyOtherEntries.Should().BeEmpty();
        result.IntoleranceOtherEntries.Should().BeEmpty();

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
    public async Task GetDailyRoster_NoOnSiteUsers_ReturnsZeroState()
    {
        var es = ActiveEvent();
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);
        _shiftRepo.GetOnSiteUserIdsForDayAsync(0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        var result = await _service.GetDailyRosterAsync(0);

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
    public async Task GetDailyRoster_OneOmnivoreVolunteerWithVEP_ReturnsCorrectAggregates()
    {
        var es = ActiveEvent();
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

        SetupRepo(es, new[] { userId }, new[] { vep });
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetDailyRosterAsync(0);

        result.TotalOnSite.Should().Be(1);
        result.UnansweredCount.Should().Be(0);
        result.DietaryBreakdown["Omnivore"].Should().Be(1);
        result.DietaryBreakdown["Unanswered"].Should().Be(0);
        result.People.Should().HaveCount(1);
        result.People[0].UserId.Should().Be(userId);
        result.People[0].BurnerName.Should().Be("AlicePrime");
        result.People[0].DietaryPreference.Should().Be("Omnivore");
    }

    [HumansFact]
    public async Task GetDailyRoster_VolunteerWithoutVEP_CountsAsUnanswered()
    {
        var es = ActiveEvent();
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Bob" };
        var profile = new Profile { UserId = userId, BurnerName = "BobBurner" };

        SetupRepo(es, new[] { userId }, Array.Empty<VolunteerEventProfile>());
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetDailyRosterAsync(0);

        result.TotalOnSite.Should().Be(1);
        result.UnansweredCount.Should().Be(1);
        result.DietaryBreakdown["Unanswered"].Should().Be(1);
        result.People.Should().HaveCount(1);
        var p = result.People[0];
        p.BurnerName.Should().Be("BobBurner");
        p.DietaryPreference.Should().BeNull();
        p.Allergies.Should().BeEmpty();
        p.AllergyOtherText.Should().BeNull();
        p.Intolerances.Should().BeEmpty();
        p.IntoleranceOtherText.Should().BeNull();
    }

    [HumansFact]
    public async Task GetDailyRoster_VolunteerWithVEPButNoDietaryPreference_CountsAsUnanswered()
    {
        var es = ActiveEvent();
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Carla" };
        var profile = new Profile { UserId = userId, BurnerName = "Carla" };
        var vep = new VolunteerEventProfile
        {
            UserId = userId,
            DietaryPreference = null,
            Allergies = new List<string>(),
            Intolerances = new List<string>()
        };

        SetupRepo(es, new[] { userId }, new[] { vep });
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetDailyRosterAsync(0);

        result.TotalOnSite.Should().Be(1);
        result.UnansweredCount.Should().Be(1);
        result.DietaryBreakdown["Unanswered"].Should().Be(1);
        result.DietaryBreakdown.Values.Where(v => v > 0).Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetDailyRoster_MixedCohort_RollsUpCorrectly()
    {
        var es = ActiveEvent();
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
        var veps = new[]
        {
            new VolunteerEventProfile
            {
                UserId = a,
                DietaryPreference = "Vegetarian",
                Allergies = new List<string> { "Peanut", "Shellfish" },
                Intolerances = new List<string> { "Lactose" }
            },
            new VolunteerEventProfile
            {
                UserId = b,
                DietaryPreference = "Vegan",
                Allergies = new List<string> { "Peanut" },
                Intolerances = new List<string>()
            },
            new VolunteerEventProfile
            {
                UserId = c,
                DietaryPreference = "Omnivore",
                Allergies = new List<string> { "Other" },
                AllergyOtherText = "MSG",
                Intolerances = new List<string>()
            }
            // user d intentionally has no VEP
        };

        SetupRepo(es, new[] { a, b, c, d }, veps);
        SetupUsers(users);
        SetupProfiles(profiles);

        var result = await _service.GetDailyRosterAsync(0);

        result.TotalOnSite.Should().Be(4);
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
    }

    [HumansFact]
    public async Task GetDailyRoster_MedicalConditionsNeverInDto()
    {
        // Compile-time guarantee — verify RosterPersonDto has no MedicalConditions property.
        var hasMedicalProp = typeof(Humans.Application.Services.Cantina.Dtos.RosterPersonDto)
            .GetProperty("MedicalConditions");
        hasMedicalProp.Should().BeNull(
            "RosterPersonDto must not expose MedicalConditions — GDPR Art.9 boundary.");

        // Runtime check: even with a populated MedicalConditions on the VEP,
        // the serialized DTO must not contain the medical-condition text.
        var es = ActiveEvent();
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

        SetupRepo(es, new[] { userId }, new[] { vep });
        SetupUsers(user);
        SetupProfiles(profile);

        var result = await _service.GetDailyRosterAsync(0);

        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("Severe peanut allergy");
        json.Should().NotContain("MedicalConditions");
    }

    [HumansFact]
    public async Task GetDailyRoster_PeopleOrderedByBurnerName()
    {
        var es = ActiveEvent();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var charlie = Guid.NewGuid();

        var users = new[]
        {
            new User { Id = alice, DisplayName = "Alice" },
            new User { Id = bob, DisplayName = "Bob" },
            new User { Id = charlie, DisplayName = "Charlie" }
        };
        var profiles = new[]
        {
            new Profile { UserId = alice, BurnerName = "Alice" },
            new Profile { UserId = bob, BurnerName = "Bob" },
            new Profile { UserId = charlie, BurnerName = "Charlie" }
        };

        // Deliberately scramble the input order to prove ordering is service-side.
        SetupRepo(es, new[] { charlie, alice, bob }, Array.Empty<VolunteerEventProfile>());
        SetupUsers(users);
        SetupProfiles(profiles);

        var result = await _service.GetDailyRosterAsync(0);

        result.People.Select(p => p.BurnerName).Should().Equal("Alice", "Bob", "Charlie");
    }

    // ---- helpers ----

    private void SetupRepo(EventSettings es, IReadOnlyList<Guid> onSiteIds, IReadOnlyList<VolunteerEventProfile> veps)
    {
        _shiftRepo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);
        _shiftRepo.GetOnSiteUserIdsForDayAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(onSiteIds));
        _shiftRepo.GetOnSiteVolunteerProfilesForDayAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
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
