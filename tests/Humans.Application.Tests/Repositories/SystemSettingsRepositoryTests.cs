using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.SystemSettings;
using Microsoft.EntityFrameworkCore;

namespace Humans.Application.Tests.Repositories;

public sealed class SystemSettingsRepositoryTests : IDisposable
{
    private readonly HumansDbContext _seedContext;
    private readonly TestDbContextFactory _factory;
    private readonly SystemSettingsRepository _repository;

    public SystemSettingsRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _factory = new TestDbContextFactory(options);
        _seedContext = _factory.CreateDbContext();
        _repository = new SystemSettingsRepository(_factory);
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
        _seedContext.SystemSettings.Add(new SystemSetting
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

        var row = await _seedContext.SystemSettings.AsNoTracking()
            .SingleAsync(setting => setting.Key == "Email:Paused", Xunit.TestContext.Current.CancellationToken);
        row.Value.Should().Be("true");
    }

    [HumansFact]
    public async Task SetValueAsync_UpdatesExistingRowInPlace()
    {
        _seedContext.SystemSettings.Add(new SystemSetting
        {
            Key = "Email:Paused",
            Value = "true",
        });
        await _seedContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _repository.SetValueAsync("Email:Paused", "false", Xunit.TestContext.Current.CancellationToken);

        var rows = await _seedContext.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == "Email:Paused")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        rows[0].Value.Should().Be("false");
    }
}
