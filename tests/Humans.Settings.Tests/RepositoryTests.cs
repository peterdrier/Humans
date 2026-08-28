using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Settings.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly SettingsDbContext _seedContext;
    private readonly TestDbContextFactory<SettingsDbContext> _factory;
    private readonly Repository _repository;

    public RepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SettingsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _factory = new TestDbContextFactory<SettingsDbContext>(options);
        _seedContext = _factory.CreateDbContext();
        _repository = new Repository(_factory);
    }

    public void Dispose()
    {
        _seedContext.Dispose();
    }

    [HumansFact]
    public async Task GetValueAsync_ReturnsNullWhenRowDoesNotExist()
    {
        var result = await _repository.GetValueAsync("Missing", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetValueAsync_ReturnsStoredValue()
    {
        _seedContext.Settings.Add(new Setting
        {
            Key = "Feature:Enabled",
            Value = "true",
        });
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repository.GetValueAsync("Feature:Enabled", Xunit.TestContext.Current.CancellationToken);

        result.Should().Be("true");
    }

    [HumansFact]
    public async Task SetValueAsync_InsertsRowWhenAbsent()
    {
        await _repository.SetValueAsync("Email:Paused", "true", Xunit.TestContext.Current.CancellationToken);

        var row = await _seedContext.Settings.AsNoTracking()
            .SingleAsync(setting => setting.Key == "Email:Paused", Xunit.TestContext.Current.CancellationToken);
        row.Value.Should().Be("true");
    }

    [HumansFact]
    public async Task SetValueAsync_UpdatesExistingRowInPlace()
    {
        _seedContext.Settings.Add(new Setting
        {
            Key = "Email:Paused",
            Value = "true",
        });
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _repository.SetValueAsync("Email:Paused", "false", Xunit.TestContext.Current.CancellationToken);

        var rows = await _seedContext.Settings.AsNoTracking()
            .Where(setting => setting.Key == "Email:Paused")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        rows[0].Value.Should().Be("false");
    }

    private static EventSettings MakeEvent(Guid id, EventSettingsStatus status) => new()
    {
        Id = id,
        EventName = "Nowhere 2026",
        Year = 2026,
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new NodaTime.LocalDate(2026, 7, 9),
        EarlyEntryCapacity = new Dictionary<int, int>(),
        Status = status,
    };

    [HumansFact]
    public async Task UpsertEventSettingsAsync_InsertsANewRowStampingBothTimestamps()
    {
        var id = Guid.NewGuid();
        var now = NodaTime.Instant.FromUtc(2026, 1, 2, 3, 4);

        await _repository.UpsertEventSettingsAsync(
            MakeEvent(id, EventSettingsStatus.Active), now, Xunit.TestContext.Current.CancellationToken);

        var row = await _seedContext.EventSettings.AsNoTracking()
            .SingleAsync(e => e.Id == id, Xunit.TestContext.Current.CancellationToken);
        row.CreatedAt.Should().Be(now);
        row.UpdatedAt.Should().Be(now);
    }

    [HumansFact]
    public async Task UpsertEventSettingsAsync_UpdatesInPlaceKeepingCreatedAt()
    {
        var id = Guid.NewGuid();
        var created = NodaTime.Instant.FromUtc(2026, 1, 1, 0, 0);
        var seed = MakeEvent(id, EventSettingsStatus.Active);
        seed.CreatedAt = created;
        seed.UpdatedAt = created;
        _seedContext.EventSettings.Add(seed);
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var incoming = MakeEvent(id, EventSettingsStatus.Inactive);
        incoming.EventName = "Nowhere 2026 (renamed)";
        var later = NodaTime.Instant.FromUtc(2026, 2, 2, 0, 0);

        await _repository.UpsertEventSettingsAsync(
            incoming, later, Xunit.TestContext.Current.CancellationToken);

        var rows = await _seedContext.EventSettings.AsNoTracking()
            .Where(e => e.Id == id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        rows[0].EventName.Should().Be("Nowhere 2026 (renamed)");
        rows[0].Status.Should().Be(EventSettingsStatus.Inactive);
        rows[0].CreatedAt.Should().Be(created);
        rows[0].UpdatedAt.Should().Be(later);
    }

    [HumansFact]
    public async Task AnyOtherActiveEventSettingsAsync_IgnoresTheRowBeingSaved()
    {
        var id = Guid.NewGuid();
        _seedContext.EventSettings.Add(MakeEvent(id, EventSettingsStatus.Active));
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repository.AnyOtherActiveEventSettingsAsync(
            id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task AnyOtherActiveEventSettingsAsync_SeesADifferentActiveRow()
    {
        _seedContext.EventSettings.Add(MakeEvent(Guid.NewGuid(), EventSettingsStatus.Active));
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repository.AnyOtherActiveEventSettingsAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task AnyOtherActiveEventSettingsAsync_IgnoresInactiveAndDeletedRows()
    {
        _seedContext.EventSettings.Add(MakeEvent(Guid.NewGuid(), EventSettingsStatus.Inactive));
        _seedContext.EventSettings.Add(MakeEvent(Guid.NewGuid(), EventSettingsStatus.Deleted));
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repository.AnyOtherActiveEventSettingsAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }
}
