using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Agent.Models;
using System.ComponentModel.DataAnnotations;
using NSubstitute;
using Humans.Agent.Data;
using Humans.Agent.Services;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Agent.Domain;

namespace Humans.Agent.Tests;

public class AgentSettingsServiceTests
{
    [HumansFact]
    public async Task Updating_persists_to_db_and_refreshes_store()
    {
        await using var db = CreateDb();
        db.AgentSettings.Add(new AgentSettings
        {
            Id = 1,
            Enabled = false,
            Model = "claude-sonnet-4-6",
            PreloadConfig = AgentPreloadConfig.Tier1,
            DailyMessageCap = 30,
            HourlyMessageCap = 10,
            DailyTokenCap = 50000,
            RetentionDays = 90,
            UpdatedAt = Instant.FromUtc(2026, 4, 21, 0, 0)
        });
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var store = new AgentSettingsStore();
        var clock = new TestClock(Instant.FromUtc(2026, 4, 22, 9, 0));
        var repo = new AgentRepository(db, clock);
        var audit = Substitute.For<IAuditLogService>();
        var service = new AgentSettingsService(repo, store, clock, audit, Substitute.For<IAnthropicClient>(),
            NullLogger<AgentSettingsService>.Instance);
        await service.LoadAsync(Xunit.TestContext.Current.CancellationToken);

        var actorId = Guid.NewGuid();
        await service.UpdateAsync(s =>
        {
            s.Enabled = true;
            s.DailyMessageCap = 60;
        }, actorId, Xunit.TestContext.Current.CancellationToken);

        store.Current.Enabled.Should().BeTrue();
        store.Current.DailyMessageCap.Should().Be(60);

        var reloaded = await db.AgentSettings.AsNoTracking().FirstAsync(Xunit.TestContext.Current.CancellationToken);
        reloaded.Enabled.Should().BeTrue();
        reloaded.DailyMessageCap.Should().Be(60);
        var entry = audit.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        entry[0].Should().Be(AgentAuditActions.AgentSettingsUpdated);
        entry[1].Should().Be("AgentSettings");
        entry[4].Should().Be(actorId);
        entry[2].Should().Be(Guid.Empty);
        entry[3]!.ToString().Should().Contain("DailyMessageCap=60").And.Contain("SettingsId=1");
    }

    [HumansTheory]
    [Xunit.InlineData(0, false)]
    [Xunit.InlineData(64, true)]
    [Xunit.InlineData(65, false)]
    public void SettingsForm_ModelMustFitTheRequiredDatabaseColumn(int length, bool valid)
    {
        var model = new AdminAgentSettingsViewModel { Model = new string('m', length) };
        var errors = new List<ValidationResult>();

        Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true)
            .Should().Be(valid);
    }

    [HumansFact]
    public async Task Model_options_list_live_models_with_prices_and_keep_the_saved_one()
    {
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(new List<AnthropicModelInfo>
        {
            new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
            new("claude-brand-new", "Claude Brand New"),
        });
        var service = CreateModelOptionsService(anthropic, savedModel: "claude-sonnet-4-6");
        var ct = Xunit.TestContext.Current.CancellationToken;

        var options = await service.GetModelOptionsAsync(ct);

        options.LiveListAvailable.Should().BeTrue();
        options.Models.Select(m => m.Id).Should().Equal("claude-sonnet-4-6", "claude-sonnet-5-5", "claude-brand-new");
        options.Models[1].Price!.Input.Should().Be(2.00m);
        options.Models[2].Price.Should().BeNull();
    }

    [HumansFact]
    public async Task Model_options_fall_back_to_the_saved_model_when_anthropic_fails()
    {
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.ListModelsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AnthropicModelInfo>>(_ => throw new HttpRequestException("down"));
        var service = CreateModelOptionsService(anthropic, savedModel: "claude-sonnet-4-6");

        var options = await service.GetModelOptionsAsync(Xunit.TestContext.Current.CancellationToken);

        options.LiveListAvailable.Should().BeFalse();
        options.Models.Should().ContainSingle().Which.Id.Should().Be("claude-sonnet-4-6");
    }

    private static AgentSettingsService CreateModelOptionsService(IAnthropicClient anthropic, string savedModel)
    {
        var store = new AgentSettingsStore();
        store.Set(new AgentSettings { Model = savedModel });
        var clock = new TestClock(Instant.FromUtc(2026, 10, 28, 9, 0));
        return new AgentSettingsService(Substitute.For<IAgentRepository>(), store, clock, Substitute.For<IAuditLogService>(),
            anthropic, NullLogger<AgentSettingsService>.Instance);
    }

    private static AgentDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AgentDbContext(options);
    }

    private sealed class TestClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
