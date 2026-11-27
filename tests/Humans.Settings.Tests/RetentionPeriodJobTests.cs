using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Settings.Jobs;
using Humans.Settings.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

public sealed class RetentionPeriodJobTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 26, 3, 40);

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public RetentionPeriodJobTests() => _clock.GetCurrentInstant().Returns(Now);

    private RetentionPeriodJob Job(params IRetentionPeriodContributor[] contributors) =>
        new(_settings, contributors, _auditLog, _clock, NullLogger<RetentionPeriodJob>.Instance);

    private static IRetentionPeriodContributor Contributor(string label, int deletes)
    {
        var contributor = Substitute.For<IRetentionPeriodContributor>();
        contributor.Label.Returns(label);
        contributor.DeleteOlderThanAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns(deletes);
        return contributor;
    }

    [HumansFact]
    public async Task ExecuteAsync_UnsetPeriod_DeletesOlderThan365Days()
    {
        var contributor = Contributor("temporary audit entries", 3);

        await Job(contributor).ExecuteAsync(TestContext.Current.CancellationToken);

        await contributor.Received(1).DeleteOlderThanAsync(Now - Duration.FromDays(365), Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(SettingsAuditActions.RetentionPeriodDeleted, AuditEntityTypes.RetentionPeriod,
            Guid.Empty, "Deleted 3 temporary audit entries older than 365 days.", RetentionPeriodJob.JobName);
    }

    [HumansFact]
    public async Task ExecuteAsync_OneContributorFails_OthersStillRunThenThrows()
    {
        _settings.GetValueAsync(RetentionPeriod.Key, Arg.Any<CancellationToken>()).Returns("30");
        var failing = Contributor("broken rows", 0);
        failing.DeleteOlderThanAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));
        var working = Contributor("old rows", 0);

        var act = () => Job(failing, working).ExecuteAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await working.Received(1).DeleteOlderThanAsync(Now - Duration.FromDays(30), Arg.Any<CancellationToken>());
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default!, default(string)!);
    }
}
