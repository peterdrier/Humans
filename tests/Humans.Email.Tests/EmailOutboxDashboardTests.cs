using AwesomeAssertions;
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
            Options.Create(new EmailSettings { OutboxBatchSize = 37 }));

        var result = await controller.EmailOutbox();

        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<EmailOutboxViewModel>().Subject.OutboxBatchSize.Should().Be(37);
    }
}
