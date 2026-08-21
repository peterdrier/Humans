using AwesomeAssertions;
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
}
