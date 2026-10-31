using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Tests.Services;

public sealed class MembershipTierServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);
    private static readonly LocalDate Today = new(2026, 10, 21);

    private readonly FakeClock _clock = new(Now);
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly GovernanceDbContext _db;
    private readonly MembershipTierService _sut;

    public MembershipTierServiceTests()
    {
        var factory = new TestDbContextFactory<GovernanceDbContext>(
            new DbContextOptionsBuilder<GovernanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db = factory.CreateDbContext();
        _sut = new MembershipTierService(
            new ApplicationRepository(factory), new ApplicationRepository(factory), _users, _clock);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid SeedUser(Guid? id = null)
    {
        var userId = id ?? Guid.NewGuid();
        var info = UserInfo.Create(
            new User { Id = userId, BurnerName = "Ana", CreatedAt = Now },
            [], [], [], UserFixtures.Profile(), []);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        return userId;
    }

    private async Task<MemberApplication> SeedApprovedAsync(Guid userId, MembershipTier tier, LocalDate? expires)
    {
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = tier,
            Motivation = "m",
            SubmittedAt = Now - Duration.FromDays(30),
            UpdatedAt = Now - Duration.FromDays(30),
        };
        app.Approve(Guid.NewGuid(), null, new FakeClock(Now - Duration.FromDays(20)));
        app.TermExpiresAt = expires;
        _db.Applications.Add(app);
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
        return app;
    }

    private async Task SeedRowAsync(Guid userId, MembershipTier tier, MemberApplication? application = null)
    {
        _db.MembershipTiers.Add(new MembershipTierRecord
        {
            UserId = userId, Tier = tier, GrantedAt = Now,
            ExpiresAt = application?.TermExpiresAt, GrantedByApplicationId = application?.Id,
        });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private Task<MembershipTierRecord?> RowAsync(Guid userId) =>
        _db.MembershipTiers.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId, Ct);

    [HumansFact]
    public async Task Read_DefaultsToVolunteer_UntilATierIsGranted()
    {
        var id = SeedUser();
        (await _sut.GetTierAsync(id, Ct)).Should().Be(MembershipTier.Volunteer);

        await SeedRowAsync(id, MembershipTier.Asociado);
        (await _sut.GetTierAsync(id, Ct)).Should().Be(MembershipTier.Asociado);
    }

    [HumansFact]
    public async Task Read_AVolunteerRow_IsExcludedFromTheTierList()
    {
        var id = SeedUser();
        await SeedRowAsync(id, MembershipTier.Volunteer);

        (await _sut.GetTierAsync(id, Ct)).Should().Be(MembershipTier.Volunteer);
        (await _sut.GetTiersAsync(Ct)).Should().NotContainKey(id);
    }

    [HumansFact]
    public async Task ReadAll_ListsOnlyNonVolunteers()
    {
        var colaborador = SeedUser();
        await SeedRowAsync(colaborador, MembershipTier.Colaborador);
        var rowed = SeedUser();
        await SeedRowAsync(rowed, MembershipTier.Asociado);
        SeedUser();

        (await _sut.GetTiersAsync(Ct)).Should().BeEquivalentTo(new Dictionary<Guid, MembershipTier>
        {
            [colaborador] = MembershipTier.Colaborador,
            [rowed] = MembershipTier.Asociado,
        });
    }

    [HumansFact]
    public async Task SyncExpiry_UpdatesTheGrantingApplicationsTerm()
    {
        var id = SeedUser();
        var app = await SeedApprovedAsync(id, MembershipTier.Colaborador, new LocalDate(2027, 12, 31));

        await SeedRowAsync(id, MembershipTier.Colaborador, app);

        await _sut.SyncExpiryAsync(app.Id, new LocalDate(2028, 12, 31), Ct);
        (await RowAsync(id))!.ExpiresAt.Should().Be(new LocalDate(2028, 12, 31));
    }

    [HumansFact]
    public async Task Expire_DowngradesLapsedHolders_KeepsInTermOnes_HonorsAnotherInTermTier()
    {
        var lapsed = SeedUser();
        await SeedRowAsync(lapsed, MembershipTier.Asociado);
        await SeedApprovedAsync(lapsed, MembershipTier.Asociado, Today.PlusDays(-1));
        var inTerm = SeedUser();
        await SeedRowAsync(inTerm, MembershipTier.Asociado);
        await SeedApprovedAsync(inTerm, MembershipTier.Asociado, Today);
        var fallsBack = SeedUser();
        await SeedRowAsync(fallsBack, MembershipTier.Asociado);
        var colaborador = await SeedApprovedAsync(fallsBack, MembershipTier.Colaborador, Today.PlusDays(400));

        var downgrades = await _sut.ExpireAsync(MembershipTier.Asociado, Today, Ct);

        downgrades.Should().BeEquivalentTo(new[]
        {
            (lapsed, MembershipTier.Volunteer),
            (fallsBack, MembershipTier.Colaborador),
        });
        (await RowAsync(lapsed))!.Tier.Should().Be(MembershipTier.Volunteer);
        (await RowAsync(inTerm))!.Tier.Should().Be(MembershipTier.Asociado);
        var fallback = await RowAsync(fallsBack);
        fallback!.Tier.Should().Be(MembershipTier.Colaborador);
        fallback.GrantedByApplicationId.Should().Be(colaborador.Id);
        fallback.ExpiresAt.Should().Be(Today.PlusDays(400));
        (await _sut.GetTierAsync(lapsed, Ct)).Should().Be(MembershipTier.Volunteer);
    }

    [HumansFact]
    public async Task Merge_MovesTheSourceTier_WhenTheSurvivorHasNone()
    {
        var source = SeedUser();
        var target = SeedUser();
        await SeedRowAsync(source, MembershipTier.Asociado);

        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct);

        (await RowAsync(source)).Should().BeNull();
        (await RowAsync(target))!.Tier.Should().Be(MembershipTier.Asociado);
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Merge_PreservesTheSourceTierAndGrant_WithoutRevivingExpiredTiers(
        bool sourceDowngraded, bool survivorDowngraded)
    {
        var source = SeedUser();
        var target = SeedUser();
        var granting = await SeedApprovedAsync(source, MembershipTier.Asociado, Today.PlusDays(600));
        if (!sourceDowngraded) await SeedRowAsync(source, granting.MembershipTier, granting);
        if (sourceDowngraded) await SeedRowAsync(source, MembershipTier.Volunteer);
        if (survivorDowngraded) await SeedRowAsync(target, MembershipTier.Volunteer);

        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct);
        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct);

        var row = await RowAsync(target);
        row.Should().NotBeNull();
        row!.Tier.Should().Be(sourceDowngraded || survivorDowngraded
            ? MembershipTier.Volunteer : MembershipTier.Asociado);
        if (!sourceDowngraded && !survivorDowngraded)
        {
            row.GrantedByApplicationId.Should().Be(granting.Id);
            row.GrantedAt.Should().Be(Now);
            row.ExpiresAt.Should().Be(granting.TermExpiresAt);
        }
        (await RowAsync(source)).Should().BeNull();
    }

    [HumansFact]
    public async Task Merge_TheSurvivorsRecordedTierWins()
    {
        var source = SeedUser();
        var target = SeedUser();
        await SeedRowAsync(target, MembershipTier.Colaborador);
        await SeedRowAsync(source, MembershipTier.Asociado);

        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct);

        (await RowAsync(source)).Should().BeNull();
        (await RowAsync(target))!.Tier.Should().Be(MembershipTier.Colaborador);
    }

    [HumansFact]
    public async Task Export_CarriesTheRow_OrNothing()
    {
        var rowed = SeedUser();
        await SeedRowAsync(rowed, MembershipTier.Asociado);
        var volunteer = SeedUser();

        var slice = (await _sut.ContributeForUserAsync(rowed, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(MembershipTierService.MembershipTierSlice);
        slice.Data.Should().BeEquivalentTo(new { Tier = MembershipTier.Asociado, GrantedAt = "2026-10-21T12:00:00Z" },
            o => o.ExcludingMissingMembers());
        (await _sut.ContributeForUserAsync(volunteer, Ct)).Single().Data.Should().BeNull();
    }

    [HumansFact]
    public async Task Erase_RetainsTheTier_AsTheRegisterOfMembers()
    {
        var id = SeedUser();
        await SeedRowAsync(id, MembershipTier.Asociado);

        await _sut.EraseForUserAsync(id, Ct);

        _sut.ErasureDeclaration[MembershipTierService.MembershipTierSlice].Should().StartWith("Retained");
        (await RowAsync(id))!.Tier.Should().Be(MembershipTier.Asociado);
    }
}
