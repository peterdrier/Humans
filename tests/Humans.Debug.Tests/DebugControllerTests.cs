using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Data;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Admin;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Logging;
using Humans.Debug.Controllers;
using Humans.Debug.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Serilog.Events;
using Serilog.Parsing;
using TestContext = Xunit.TestContext;

namespace Humans.Debug.Tests;

/// <summary>
/// The three things a diagnostics page can get wrong for real: leaking a secret, reading an
/// unbounded buffer, and clearing infrastructure state without a trace in production logs.
/// </summary>
public class DebugControllerTests
{
    private readonly IClientStatsTracker _clientStats = Substitute.For<IClientStatsTracker>();
    private readonly IAdminDatabaseDiagnosticsService _diagnostics = Substitute.For<IAdminDatabaseDiagnosticsService>();
    private readonly CapturingLogger<DebugController> _logger = new();
    private readonly ConfigurationRegistry _configuration = new();
    private readonly InMemoryLogSink _logSink = new();

    public DebugControllerTests()
    {
        _clientStats.GetErrorsSnapshot(Arg.Any<int>())
            .Returns(new ClientErrorsSnapshot(0, new Dictionary<int, long>(), []));
    }

    [HumansFact]
    public void Configuration_masks_sensitive_values_to_a_four_char_prefix()
    {
        _configuration.Register(Entry("Email:Password", isSensitive: true, value: "abcdefgh"));
        _configuration.Register(Entry("Email:Pin", isSensitive: true, value: "abcd"));
        _configuration.Register(Entry("Email:Host", isSensitive: false, value: "smtp.example.org"));
        _configuration.Register(Entry("Email:Unset", isSensitive: true, value: null));

        var model = BuildSut().Configuration().Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<AdminConfigurationViewModel>().Subject;

        model.Items.Select(i => (i.Key, i.DisplayValue)).Should().BeEquivalentTo(
        [
            ("Email:Password", "abcd******"),
            ("Email:Pin", "******"),
            ("Email:Host", "smtp.example.org"),
            ("Email:Unset", "(not set)"),
        ]);
    }

    [HumansFact]
    public void HttpErrors_clamps_count_into_one_to_one_thousand()
    {
        var sut = BuildSut();

        sut.HttpErrors(count: 99_999);
        sut.HttpErrors(count: -5);

        _clientStats.Received(1).GetErrorsSnapshot(1000);
        _clientStats.Received(1).GetErrorsSnapshot(1);
    }

    [HumansFact]
    public void Logs_clamps_count_into_one_to_one_thousand()
    {
        for (var i = 0; i < 3; i++)
            _logSink.Emit(new LogEvent(DateTimeOffset.UtcNow.AddSeconds(i), LogEventLevel.Warning, null,
                new MessageTemplateParser().Parse("warning {N}"), []));

        var sut = BuildSut();

        var floor = sut.Logs(count: -5).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeAssignableTo<IReadOnlyList<LogEvent>>().Subject;
        var ceiling = sut.Logs(count: 99_999).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeAssignableTo<IReadOnlyList<LogEvent>>().Subject;

        floor.Should().HaveCount(1);
        ceiling.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task ClearHangfireLocks_logs_at_warning_and_returns_to_maintenance()
    {
        _diagnostics.ClearHangfireLocksAsync(Arg.Any<CancellationToken>()).Returns(3);

        var result = await BuildSut().ClearHangfireLocks(TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(DebugController.Maintenance));
        _logger.Entries.Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Warning, because: "the clear must reach production logs");
    }

    private DebugController BuildSut()
    {
        var controller = new DebugController(
            Substitute.For<IUserServiceRead>(),
            _clientStats,
            Substitute.For<IHttpStatusTracker>(),
            _logger,
            _configuration,
            new QueryStatistics(),
            Substitute.For<ICacheStatsProvider>(),
            [],
            _diagnostics,
            Substitute.For<ISectionCatalog>(),
            _logSink);

        var http = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static ConfigurationEntry Entry(string key, bool isSensitive, string? value) => new()
    {
        Section = "Email",
        Key = key,
        IsSensitive = isSensitive,
        Importance = ConfigurationImportance.Recommended,
        IsSet = value is not null,
        Value = value,
    };
}
