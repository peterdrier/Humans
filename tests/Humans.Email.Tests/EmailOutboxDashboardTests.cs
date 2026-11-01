using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;
using Humans.Base.Configuration;
using Humans.Email.Controllers;
using Humans.Email.Models;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NodaTime;

namespace Humans.Email.Tests;

public sealed class EmailOutboxDashboardTests
{
    [HumansFact]
    public async Task EmailOutbox_UsesConfiguredBatchSizeAndOrdersDailyCounts()
    {
        var outbox = Substitute.For<IEmailOutboxManagement>();
        outbox.GetOutboxStatsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EmailOutboxStats(0, 0, 0, 0, false, []));
        var day = new LocalDate(2026, 8, 15);
        outbox.GetDailySendCountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DailySendCountsDto(
                [new DailySendCountRow(day, 3, 1), new DailySendCountRow(day.PlusDays(1), 5, 0)], []));
        var controller = new EmailController(
            Substitute.For<IUserService>(), outbox, NullLogger<EmailController>.Instance,
            Options.Create(new EmailSettings { OutboxBatchSize = 37 }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.EmailOutbox();

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<EmailOutboxViewModel>().Subject;
        model.OutboxBatchSize.Should().Be(37);
        model.DailyCounts.Select(r => r.Date).Should().Equal(day.PlusDays(1), day);
    }
    [HumansTheory]
    [InlineData("stats")]
    [InlineData("daily")]
    [InlineData("backfill")]
    public async Task DashboardReads_ObserveRequestCancellation(string boundary)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var outbox = Substitute.For<IEmailOutboxManagement>();
        outbox.GetOutboxStatsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EmailOutboxStats(0, 0, 0, 0, false, []));
        outbox.GetDailySendCountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DailySendCountsDto([], []));
        outbox.PreviewDailySendCountBackfillAsync(Arg.Any<CancellationToken>())
            .Returns(new BackfillPreviewDto(0, null, null, []));
        if (string.Equals(boundary, "stats", StringComparison.Ordinal))
            outbox.GetOutboxStatsAsync(Arg.Any<int>(), aborted.Token)
                .Returns(Task.FromCanceled<EmailOutboxStats>(aborted.Token));
        else if (string.Equals(boundary, "daily", StringComparison.Ordinal))
            outbox.GetDailySendCountsAsync(Arg.Any<int>(), aborted.Token)
                .Returns(Task.FromCanceled<DailySendCountsDto>(aborted.Token));
        else
            outbox.PreviewDailySendCountBackfillAsync(aborted.Token)
                .Returns(Task.FromCanceled<BackfillPreviewDto>(aborted.Token));
        var controller = new EmailController(
            Substitute.For<IUserService>(), outbox, NullLogger<EmailController>.Instance, Options.Create(new EmailSettings()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
        };

        var act = () => string.Equals(boundary, "backfill", StringComparison.Ordinal)
            ? controller.BackfillDailyCountsPreview() : controller.EmailOutbox();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

}
