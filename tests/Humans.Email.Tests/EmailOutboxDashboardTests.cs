using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Models;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Email.Tests;

public sealed class EmailOutboxDashboardTests
{
    [HumansFact]
    public async Task EmailOutbox_UsesConfiguredBatchSize()
    {
        var outbox = Substitute.For<IEmailOutboxService>();
        outbox.GetOutboxStatsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EmailOutboxStats(0, 0, 0, 0, false, []));
        outbox.GetDailySendCountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DailySendCountsDto([], []));
        var controller = new EmailController(
            Substitute.For<IUserServiceRead>(), outbox, Substitute.For<IAuditLogService>(),
            NullLogger<EmailController>.Instance,
            Options.Create(new EmailSettings { OutboxBatchSize = 37 }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.EmailOutbox();

        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<EmailOutboxViewModel>().Subject.OutboxBatchSize.Should().Be(37);
    }
    [HumansTheory]
    [InlineData("stats")]
    [InlineData("daily")]
    [InlineData("backfill")]
    public async Task DashboardReads_ObserveRequestCancellation(string boundary)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var outbox = Substitute.For<IEmailOutboxService>();
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
            Substitute.For<IUserServiceRead>(), outbox, Substitute.For<IAuditLogService>(),
            NullLogger<EmailController>.Instance, Options.Create(new EmailSettings()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
        };

        var act = () => string.Equals(boundary, "backfill", StringComparison.Ordinal)
            ? controller.BackfillDailyCountsPreview() : controller.EmailOutbox();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

}
