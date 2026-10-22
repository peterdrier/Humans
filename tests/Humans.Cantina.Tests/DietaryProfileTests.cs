using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Cantina.Contracts;
using Humans.Cantina.Data;
using Humans.Cantina.Domain;
using Humans.Cantina.Services;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Cantina.Tests;

/// <summary>
/// cantina_dietary_profiles: the read with its legacy fallback,
/// the member's save and "I don't want to answer" opt-out, the prompt rule, the
/// cantina.profile-dietary copy, merge, export and erasure, and the dashboard nudge.
/// </summary>
public class DietaryProfileTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IDietaryProfileRepository _repo;
    private readonly DietaryProfileService _sut;

    public DietaryProfileTests()
    {
        var options = new DbContextOptionsBuilder<CantinaDbContext>()
            .UseInMemoryDatabase($"cantina-diet-{Guid.NewGuid():N}")
            .Options;
        _repo = new DietaryProfileRepository(new Factory(options));
        _sut = new DietaryProfileService(_repo, _users, new FakeClock(Now));
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns([]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
    }

    private sealed class Factory(DbContextOptions<CantinaDbContext> options) : IDbContextFactory<CantinaDbContext>
    {
        public CantinaDbContext CreateDbContext() => new(options);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly List<UserInfo> _all = [];

    /// <summary>A member whose legacy profiles columns hold the given food answers.</summary>
    private UserInfo SeedUser(Guid id, string? legacyPreference = null, IReadOnlyList<string>? legacyAllergies = null,
        string? legacyMedical = null, Guid? resolvesTo = null)
    {
        var info = UserInfo.Create(
            new User { Id = resolvesTo ?? id, BurnerName = "Ana", CreatedAt = Now },
            [], [], [],
            UserFixtures.Profile(dietaryPreference: legacyPreference, allergies: legacyAllergies, medicalConditions: legacyMedical),
            []);
        _users.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(info);
        _users.GetRawUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(info);
        if (resolvesTo is null)
        {
            _all.Add(info);
            _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(_all.ToList());
        }
        return info;
    }

    private static DietaryProfileInfo Answers(Guid id, string? preference = "Vegan",
        IReadOnlyList<string>? allergies = null, string? allergyOther = null,
        IReadOnlyList<string>? intolerances = null, string? intoleranceOther = null) =>
        new(id, preference, allergies ?? [], allergyOther, intolerances ?? [], intoleranceOther);

    [HumansFact]
    public async Task Read_FallsBackToTheLegacyProfile_UntilARowExists()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyPreference: "Omnivore", legacyAllergies: ["Egg"]);

        var legacy = await _sut.GetDietaryProfileAsync(id, Ct);
        legacy!.DietaryPreference.Should().Be("Omnivore");
        legacy.Allergies.Should().Equal("Egg");

        (await _sut.SaveAsync(id, Answers(id), Ct)).Should().BeNull();
        var saved = await _sut.GetDietaryProfileAsync(id, Ct);
        saved!.DietaryPreference.Should().Be("Vegan");
        saved.Allergies.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Read_NoRowAndNoLegacyAnswers_IsNull()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyMedical: "Epilepsy");

        (await _sut.GetDietaryProfileAsync(id, Ct)).Should().BeNull("medical conditions are not a food answer");
    }

    [HumansFact]
    public async Task Save_ClearingThePreference_RecordsIt_SoTheLegacyValueDoesNotComeBack()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyPreference: "Omnivore");

        await _sut.SaveAsync(id, Answers(id, preference: "  "), Ct);

        var read = await _sut.GetDietaryProfileAsync(id, Ct);
        read!.DietaryPreference.Should().BeNull();
        read.HasPreference.Should().BeFalse();
    }

    [HumansFact]
    public async Task Save_Normalizes_UnknownChipsDropped_OtherTextOnlyWithOther()
    {
        var id = Guid.NewGuid();
        SeedUser(id);

        await _sut.SaveAsync(id, Answers(id,
            allergies: ["Peanut", "Kryptonite", "Peanut"], allergyOther: "ignored",
            intolerances: ["Other"], intoleranceOther: "  Fructose  "), Ct);

        var read = await _sut.GetDietaryProfileAsync(id, Ct);
        read!.Allergies.Should().Equal("Peanut");
        read.AllergyOtherText.Should().BeNull();
        read.Intolerances.Should().Equal(DietaryOptions.OtherOption);
        read.IntoleranceOtherText.Should().Be("Fructose");
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Save_OtherWithoutText_IsRefused_AndNothingIsWritten(bool allergy)
    {
        var id = Guid.NewGuid();
        SeedUser(id);

        var error = await _sut.SaveAsync(id, allergy
            ? Answers(id, allergies: ["Other"], allergyOther: " ")
            : Answers(id, intolerances: ["Other"]), Ct);

        error.Should().Be(allergy ? "Cantina_Dietary_AllergyOther_Required" : "Cantina_Dietary_IntoleranceOther_Required");
        (await _repo.GetAsync(id, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Read_WithAMergedAwayId_ReadsTheSurvivorsRow()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        SeedUser(survivor);
        SeedUser(archived, resolvesTo: survivor);
        await _sut.SaveAsync(survivor, Answers(survivor, preference: "Pescatarian"), Ct);

        (await _sut.GetDietaryProfileAsync(archived, Ct))!.DietaryPreference.Should().Be("Pescatarian");
    }

    [HumansFact]
    public async Task BulkRead_IsKeyedByTheRequestedId_RowsThenLegacy_MembersWithNothingAbsent()
    {
        var withRow = Guid.NewGuid();
        var legacyOnly = Guid.NewGuid();
        var nothing = Guid.NewGuid();
        var infos = new Dictionary<Guid, UserInfo>
        {
            [withRow] = SeedUser(withRow, legacyPreference: "Omnivore"),
            [legacyOnly] = SeedUser(legacyOnly, legacyPreference: "Vegetarian"),
            [nothing] = SeedUser(nothing),
        };
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(infos));
        await _sut.SaveAsync(withRow, Answers(withRow), Ct);

        var read = await _sut.GetDietaryProfilesAsync([withRow, legacyOnly, nothing], Ct);

        read.Keys.Should().BeEquivalentTo([withRow, legacyOnly]);
        read[withRow].DietaryPreference.Should().Be("Vegan");
        read[legacyOnly].DietaryPreference.Should().Be("Vegetarian");
    }

    [HumansFact]
    public async Task Decline_IsReadBackAsTheOptOutFact_WithNoPreference()
    {
        var id = Guid.NewGuid();
        SeedUser(id);
        (await _sut.GetDietaryProfileAsync(id, Ct)).Should().BeNull();

        await _sut.DeclineAsync(id, Ct);

        var read = await _sut.GetDietaryProfileAsync(id, Ct);
        read!.DeclinedToAnswer.Should().BeTrue();
        read.HasPreference.Should().BeFalse();
    }

    [HumansFact]
    public async Task LegacyOnlyPreference_ReadsAsAPreference_NotAnOptOut()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyPreference: "Omnivore");

        var read = await _sut.GetDietaryProfileAsync(id, Ct);
        read!.HasPreference.Should().BeTrue();
        read.DeclinedToAnswer.Should().BeFalse();
    }

    [HumansFact]
    public async Task Decline_ClearsEveryAnswer_AndSavingAnswersLaterClearsTheDecline()
    {
        var id = Guid.NewGuid();
        SeedUser(id);
        await _sut.SaveAsync(id, Answers(id, allergies: ["Egg"], intolerances: ["Other"], intoleranceOther: "Fructose"), Ct);

        await _sut.DeclineAsync(id, Ct);

        var declined = await _repo.GetAsync(id, Ct);
        declined!.DeclinedToAnswer.Should().BeTrue();
        declined.DietaryPreference.Should().BeNull();
        declined.Allergies.Should().BeEmpty();
        declined.Intolerances.Should().BeEmpty();
        declined.IntoleranceOtherText.Should().BeNull();

        await _sut.SaveAsync(id, Answers(id, preference: "Vegetarian"), Ct);

        var answered = await _sut.GetDietaryProfileAsync(id, Ct);
        answered!.DeclinedToAnswer.Should().BeFalse();
        answered.DietaryPreference.Should().Be("Vegetarian");
    }

    [HumansFact]
    public async Task Migration_ListsMembersWithLegacyAnswersAndNoRow_CopiesThem_AndIsIdempotent()
    {
        var pending = Guid.NewGuid();
        var alreadyCopied = Guid.NewGuid();
        SeedUser(pending, legacyPreference: "Vegan", legacyAllergies: ["Soy"]);
        SeedUser(alreadyCopied, legacyPreference: "Omnivore");
        SeedUser(Guid.NewGuid(), legacyMedical: "Diabetes");
        await _sut.SaveAsync(alreadyCopied, Answers(alreadyCopied, preference: "Pescatarian"), Ct);
        var migration = new SectionDataMigrations().Migrations().Single();
        var sp = new ServiceCollection().AddSingleton<IDietaryProfileManagement>(_sut).BuildServiceProvider();

        migration.Key.Should().Be("cantina.profile-dietary");
        var status = await migration.Status(sp, Ct);
        status.PendingRows.Should().ContainSingle().Which[0].Should().Be(pending.ToString());
        status.Columns.Should().NotContain(c => c.Contains("Medical", StringComparison.OrdinalIgnoreCase));

        (await migration.Apply(sp, Guid.NewGuid(), Ct)).Should().Be(1);
        (await _repo.GetAsync(pending, Ct))!.Allergies.Should().Equal("Soy");
        (await _repo.GetAsync(alreadyCopied, Ct))!.DietaryPreference.Should().Be("Pescatarian", "a saved row is never overwritten");
        (await migration.Status(sp, Ct)).Done.Should().BeTrue();
        (await migration.Apply(sp, Guid.NewGuid(), Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task Merge_MovesTheSourceRow_WhenTheSurvivorHasNoAnswers()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        SeedUser(from);
        SeedUser(to);
        await _sut.SaveAsync(from, Answers(from, preference: "Vegetarian"), Ct);

        await _sut.ReassignAsync(from, to, Guid.NewGuid(), Now, Ct);

        (await _repo.GetAsync(from, Ct)).Should().BeNull();
        (await _repo.GetAsync(to, Ct))!.DietaryPreference.Should().Be("Vegetarian");
    }

    [HumansFact]
    public async Task Merge_TheSurvivorWins_IncludingAnswersStillOnItsLegacyProfile()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        SeedUser(from);
        SeedUser(to, legacyPreference: "Omnivore");
        await _sut.SaveAsync(from, Answers(from, preference: "Vegetarian"), Ct);

        await _sut.ReassignAsync(from, to, Guid.NewGuid(), Now, Ct);

        (await _repo.GetAsync(from, Ct)).Should().BeNull();
        (await _repo.GetAsync(to, Ct))!.DietaryPreference.Should().Be("Omnivore");
    }

    [HumansFact]
    public async Task Export_CarriesTheFoodAnswers_NeverMedical_AndEraseDeletesTheRow()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyMedical: "Asthma");
        await _sut.SaveAsync(id, Answers(id, allergies: ["Egg"]), Ct);

        var slice = (await _sut.ContributeForUserAsync(id, Ct)).Single();
        slice.SectionName.Should().Be(DietaryProfileService.DietaryProfileSlice);
        var json = System.Text.Json.JsonSerializer.Serialize(slice.Data);
        json.Should().Contain("Vegan").And.Contain("Egg").And.NotContain("Asthma")
            .And.Contain("\"DeclinedToAnswer\":false");
        _sut.ErasureDeclaration.Should().ContainKey(DietaryProfileService.DietaryProfileSlice);

        await _sut.EraseForUserAsync(id, Ct);

        (await _repo.GetAsync(id, Ct)).Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(false, "Todo_DietaryMedical_NoShift_Pending")]
    [Xunit.InlineData(true, "Todo_DietaryMedical_Pending")]
    public async Task Nudge_ShowsWhileNoPreference_CopyFollowsTheQualifyingSignup(bool qualifying, string copy)
    {
        var id = Guid.NewGuid();
        SeedUser(id);
        var shifts = Substitute.For<IShiftService>();
        shifts.HasQualifyingCantinaSignupAsync(id, Arg.Any<CancellationToken>()).Returns(qualifying);

        var entries = await new SectionThingsToDo().EntriesAsync(NudgeServices(shifts), Principal(id));

        var entry = entries.Should().ContainSingle().Subject;
        entry.Key.Should().Be("dietary");
        entry.Controller.Should().Be("CantinaProfile");
        entry.Action.Should().Be("Dietary");
        entry.Description.Should().Be(copy);
    }

    [HumansFact]
    public async Task Nudge_IsGoneOnceAPreferenceIsOnFile()
    {
        var id = Guid.NewGuid();
        SeedUser(id);
        await _sut.SaveAsync(id, Answers(id), Ct);

        var entries = await new SectionThingsToDo().EntriesAsync(
            NudgeServices(Substitute.For<IShiftService>()), Principal(id));

        entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Nudge_IsGoneAfterTheOptOut()
    {
        var id = Guid.NewGuid();
        SeedUser(id);
        await _sut.DeclineAsync(id, Ct);

        var entries = await new SectionThingsToDo().EntriesAsync(
            NudgeServices(Substitute.For<IShiftService>()), Principal(id));

        entries.Should().BeEmpty();
    }

    private IServiceProvider NudgeServices(IShiftService shifts)
    {
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        return new ServiceCollection()
            .AddSingleton<ICantinaService>(_sut)
            .AddSingleton(shifts)
            .AddSingleton(localizer)
            .BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
}
