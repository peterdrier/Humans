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
        var result = await _repository.GetValueAsync("Missing");

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
        await _seedContext.SaveChangesAsync();

        var result = await _repository.GetValueAsync("Feature:Enabled");

        result.Should().Be("true");
    }

    [HumansFact]
    public async Task SetValueAsync_InsertsRowWhenAbsent()
    {
        await _repository.SetValueAsync("Email:Paused", "true");

        var row = await _seedContext.SystemSettings.AsNoTracking()
            .SingleAsync(setting => setting.Key == "Email:Paused");
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
        await _seedContext.SaveChangesAsync();

        await _repository.SetValueAsync("Email:Paused", "false");

        var rows = await _seedContext.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == "Email:Paused")
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Value.Should().Be("false");
    }
}
