using AwesomeAssertions;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Consent.Services;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

/// <summary>
/// consent_reminders: the cooldown read with its legacy users-column fallback, the stamp write,
/// the consent.reminder-stamps copy, export and erasure.
/// </summary>
public sealed class ConsentReminderServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);
    private static readonly Instant Earlier = Instant.FromUtc(2026, 10, 1, 9, 0);

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly List<UserInfo> _all = [];
    private readonly LegalDbContext _db;
    private readonly ConsentReminderService _sut;

    public ConsentReminderServiceTests()
    {
        var factory = new TestDbContextFactory<LegalDbContext>(
            new DbContextOptionsBuilder<LegalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db = factory.CreateDbContext();
        _sut = new ConsentReminderService(new ConsentReminderRepository(factory), _users);
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(_ => _all.ToList());
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A member whose frozen <c>users.LastConsentReminderSentAt</c> holds <paramref name="legacy"/>.</summary>
    private UserInfo SeedUser(Instant? legacy = null, Instant? mergedAt = null)
    {
        var info = UserInfo.Create(
            new User { Id = Guid.NewGuid(), BurnerName = "Ana", CreatedAt = Now, LastConsentReminderSentAt = legacy, MergedAt = mergedAt },
            [], [], [], null, []);
        _users.GetUserInfoAsync(info.Id, Arg.Any<CancellationToken>()).Returns(info);
        _all.Add(info);
        return info;
    }

    private async Task SeedRowAsync(Guid userId, Instant sentAt)
    {
        _db.ConsentReminders.Add(new ConsentReminder { UserId = userId, LastSentAt = sentAt });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private Task<ConsentReminder?> RowAsync(Guid userId) =>
        _db.ConsentReminders.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId, Ct);

    // ─── Read, write ─────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetLastSent_RowWins_LegacyFillsIn_NeverRemindedIsAbsent()
    {
        var withRow = SeedUser(legacy: Earlier);
        var legacyOnly = SeedUser(legacy: Earlier);
        var never = SeedUser();
        await SeedRowAsync(withRow.Id, Now);

        var result = await _sut.GetLastSentAsync([withRow, legacyOnly, never], Ct);

        result.Should().BeEquivalentTo(new Dictionary<Guid, Instant>
        {
            [withRow.Id] = Now,
            [legacyOnly.Id] = Earlier,
        });
    }

    [HumansFact]
    public async Task RecordSent_InsertsThenOverwrites_AndOutranksTheLegacyStamp()
    {
        var user = SeedUser(legacy: Earlier);

        await _sut.RecordSentAsync(user.Id, Earlier.Plus(Duration.FromDays(1)), Ct);
        await _sut.RecordSentAsync(user.Id, Now, Ct);

        (await RowAsync(user.Id))!.LastSentAt.Should().Be(Now);
        (await _sut.GetLastSentAsync([user], Ct))[user.Id].Should().Be(Now);
    }

    // ─── consent.reminder-stamps ─────────────────────────────────────────────────

    [HumansFact]
    public async Task Copy_ListsLivingUncopiedLegacyStamps_SkipsTombstones_ThenIsIdempotent()
    {
        var legacy = SeedUser(legacy: Earlier);
        var alreadyCopied = SeedUser(legacy: Earlier);
        SeedUser(legacy: Earlier, mergedAt: Earlier);
        SeedUser();
        await SeedRowAsync(alreadyCopied.Id, Now);

        (await _sut.GetUncopiedLegacyStampsAsync(Ct)).Should().BeEquivalentTo(
            [new ConsentReminderService.LegacyStamp(legacy.Id, Earlier)]);

        (await _sut.CopyLegacyStampsAsync(Ct)).Should().Be(1);
        (await _sut.CopyLegacyStampsAsync(Ct)).Should().Be(0);
        (await _sut.GetUncopiedLegacyStampsAsync(Ct)).Should().BeEmpty();
        (await RowAsync(legacy.Id))!.LastSentAt.Should().Be(Earlier);
        (await RowAsync(alreadyCopied.Id))!.LastSentAt.Should().Be(Now);
    }

    [HumansFact]
    public async Task DataMigration_IsRegistered_AndRunsTheCopy()
    {
        SeedUser(legacy: Earlier);
        var services = new ServiceCollection().AddSingleton(_sut).BuildServiceProvider();
        var migration = new SectionDataMigrations().Migrations()
            .Should().ContainSingle(m => m.Key == "consent.reminder-stamps").Subject;

        migration.Section.Should().Be("Consent");
        (await migration.Status(services, Ct)).Pending.Should().Be(1);
        (await migration.Apply(services, Guid.NewGuid(), Ct)).Should().Be(1);
        (await migration.Status(services, Ct)).Pending.Should().Be(0);
    }

    // ─── Export, erasure ─────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Export_CarriesTheRow_OrTheLegacyStamp_OrNothing()
    {
        var withRow = SeedUser(legacy: Earlier);
        var legacyOnly = SeedUser(legacy: Earlier);
        var never = SeedUser();
        await SeedRowAsync(withRow.Id, Now);

        var slice = (await _sut.ContributeForUserAsync(withRow.Id, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(ConsentReminderService.ConsentReminderSlice);
        slice.Data.Should().BeEquivalentTo(new[] { new { UserId = withRow.Id, LastSentAt = "2026-10-21T12:00:00Z" } });
        (await _sut.ContributeForUserAsync(legacyOnly.Id, Ct)).Single().Data
            .Should().BeEquivalentTo(new[] { new { UserId = legacyOnly.Id, LastSentAt = "2026-10-01T09:00:00Z" } });
        (await _sut.ContributeForUserAsync(never.Id, Ct)).Single().Data.Should().BeNull();
    }

    [HumansFact]
    public async Task Erase_DeletesTheRow()
    {
        var user = SeedUser();
        await SeedRowAsync(user.Id, Now);

        await _sut.EraseForUserAsync(user.Id, Ct);

        _sut.ErasureDeclaration[ConsentReminderService.ConsentReminderSlice].Should().BeNull();
        (await RowAsync(user.Id)).Should().BeNull();
    }
}
