using Humans.MailerLite.Jobs;
using Humans.MailerLite.Services;
using Humans.MailerLite.Services.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.MailerLite.Tests.Jobs;

public class MailerLiteAudienceSyncJobTests
{
    [HumansTheory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task ExecuteAsync_OnlySucceedsWhenAllAudiencesCompleteWithoutErrors(
        bool includesSecondAudience, int operationErrors)
    {
        var sync = Substitute.For<IMailerLiteAudienceSyncService>();
        var first = Substitute.For<IMailerLiteAudience>();
        first.Key.Returns("first");
        var second = Substitute.For<IMailerLiteAudience>();
        second.Key.Returns("second");
        var results = new List<AudienceSyncResult>
        {
            new("first", "g1", "Humans - First", 1, 0, 0, 0, 1, 0, operationErrors),
        };
        if (includesSecondAudience)
            results.Add(new("second", "g2", "Humans - Second", 1, 0, 0, 0, 1, 0, 0));
        sync.SyncAllAsync(null, Arg.Any<CancellationToken>()).Returns(results);
        var job = new MailerLiteAudienceSyncJob(sync, [first, second],
            NullLogger<MailerLiteAudienceSyncJob>.Instance);

        if (includesSecondAudience && operationErrors == 0)
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => job.ExecuteAsync(TestContext.Current.CancellationToken));
            Assert.Contains($"{(includesSecondAudience ? 0 : 1)} audiences failed", error.Message);
            Assert.Contains($"{operationErrors} subscriber operations failed", error.Message);
        }

        await sync.Received(1).SyncAllAsync(null, TestContext.Current.CancellationToken);
    }
}
