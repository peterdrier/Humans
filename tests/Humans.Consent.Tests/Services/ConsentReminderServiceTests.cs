using AwesomeAssertions;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Consent.Services;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

public sealed class ConsentReminderServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);
    private static readonly Instant Earlier = Instant.FromUtc(2026, 10, 1, 9, 0);

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly LegalDbContext _db;
    private readonly ConsentReminderService _sut;

    public ConsentReminderServiceTests()
    {
        var factory = new TestDbContextFactory<LegalDbContext>(
            new DbContextOptionsBuilder<LegalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db = factory.CreateDbContext();
        _sut = new ConsentReminderService(new ConsentReminderRepository(factory), _users);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserInfo SeedUser()
    {
        var id = Guid.NewGuid();
        var info = UserInfo.Create(
            new User
            {
                Id = id,
                BurnerName = "Ana",
                CreatedAt = Now,
                Email = "ana@example.test",
            },
            [], [], [], null, []);
        _users.GetUserInfoAsync(info.Id, Arg.Any<CancellationToken>()).Returns(info);
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

    [HumansFact]
    public async Task GetLastSent_ReturnsOwnedRows_NeverRemindedIsAbsent()
    {
        var withRow = SeedUser();
        var never = SeedUser();
        await SeedRowAsync(withRow.Id, Now);

        var result = await _sut.GetLastSentAsync([withRow, never], Ct);

        result.Should().BeEquivalentTo(new Dictionary<Guid, Instant>
        {
            [withRow.Id] = Now,
        });
    }

    [HumansFact]
    public async Task RecordSent_InsertsThenOverwrites()
    {
        var user = SeedUser();

        await _sut.RecordSentAsync(user.Id, Earlier.Plus(Duration.FromDays(1)), Ct);
        await _sut.RecordSentAsync(user.Id, Now, Ct);

        (await RowAsync(user.Id))!.LastSentAt.Should().Be(Now);
        (await _sut.GetLastSentAsync([user], Ct))[user.Id].Should().Be(Now);
    }

    [HumansFact]
    public async Task Export_IncludesSurvivorAndMergedStamps_OrNothing()
    {
        var archivedId = Guid.NewGuid();
        var withRow = SeedUser() with { MergedUserIds = [archivedId] };
        _users.GetUserInfoAsync(withRow.Id, Arg.Any<CancellationToken>()).Returns(withRow);
        var never = SeedUser();
        await SeedRowAsync(withRow.Id, Now);
        await SeedRowAsync(archivedId, Earlier);

        var slice = (await _sut.ContributeForUserAsync(withRow.Id, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(ConsentReminderService.ConsentReminderSlice);
        slice.Data.Should().BeEquivalentTo(new[]
        {
            new { UserId = withRow.Id, LastSentAt = "2026-10-21T12:00:00Z" },
            new { UserId = archivedId, LastSentAt = "2026-10-01T09:00:00Z" },
        }, options => options.WithStrictOrdering());
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
