using Microsoft.Extensions.Logging;
using Humans.Governance.Services;
using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Jobs;
using Humans.Governance.Services.Dtos;
using Humans.Governance.Tests.Infrastructure;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Governance.Tests.Services;

public sealed class TermRenewalReminderJobTests
{
    [HumansTheory]
    [Xunit.InlineData("Applications")]
    [Xunit.InlineData("Pending")]
    [Xunit.InlineData("Applicants")]
    public async Task ReminderShutdown_PropagatesInitialReadCancellation(string boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var run = new ReminderRun();
        var failure = new OperationCanceledException(shutdown.Token);
        if (string.Equals(boundary, "Applications", StringComparison.Ordinal))
            run.Applications.GetExpiringApplicationsNeedingReminderAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), shutdown.Token)
                .Returns(async Task<IReadOnlyList<ApplicationRenewalReminderCandidate>> (_) =>
                { await shutdown.CancelAsync(); throw failure; });
        else if (string.Equals(boundary, "Pending", StringComparison.Ordinal))
            run.Applications.GetPendingApplicationUserTiersAsync(shutdown.Token)
                .Returns(async Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> (_) =>
                { await shutdown.CancelAsync(); throw failure; });
        else
            run.Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), shutdown.Token)
                .Returns(async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> (_) =>
                { await shutdown.CancelAsync(); throw failure; });

        var error = await Xunit.Record.ExceptionAsync(() => run.Job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        await run.Email.DidNotReceiveWithAnyArgs().SendAsync(default!);
        run.Metrics.DidNotReceive().RecordJobRun("term_renewal_reminder", "success");
        run.Metrics.DidNotReceive().RecordJobRun("term_renewal_reminder", "failure");
        run.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
        run.Logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
    }

    [HumansTheory]
    [Xunit.InlineData("Email")]
    [Xunit.InlineData("Stamp")]
    [Xunit.InlineData("Notice")]
    public async Task ReminderShutdown_FinishesStartedRecipientBeforeStopping(string boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var run = new ReminderRun();
        run.Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (string.Equals(boundary, "Email", StringComparison.Ordinal)) await shutdown.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
        });
        run.Applications.MarkRenewalReminderSentAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (string.Equals(boundary, "Stamp", StringComparison.Ordinal)) await shutdown.CancelAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            });
        run.Notices.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).Returns(async call =>
            {
                if (string.Equals(boundary, "Notice", StringComparison.Ordinal)) await shutdown.CancelAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            });

        var error = await Xunit.Record.ExceptionAsync(() => run.Job.ExecuteAsync(shutdown.Token));

        error.Should().BeAssignableTo<OperationCanceledException>();
        await run.Email.Received(1).SendAsync(Arg.Is<EmailMessage>(message =>
            message.UserId == run.First.UserId && message.DeliveryId == run.First.Id), CancellationToken.None);
        await run.Email.DidNotReceive().SendAsync(Arg.Is<EmailMessage>(message => message.UserId == run.Next.UserId),
            Arg.Any<CancellationToken>());
        await run.Applications.Received(1).MarkRenewalReminderSentAsync(run.First.Id, run.Now, CancellationToken.None);
        await run.Applications.DidNotReceive().MarkRenewalReminderSentAsync(run.Next.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        var notice = run.Notices.ReceivedCalls().Should().ContainSingle().Which.GetArguments();
        ((IReadOnlyList<Guid>)notice[4]!).Should().ContainSingle().Which.Should().Be(run.First.UserId);
        notice[^1].Should().Be(CancellationToken.None);
        run.Metrics.DidNotReceive().RecordJobRun("term_renewal_reminder", "success");
        run.Metrics.DidNotReceive().RecordJobRun("term_renewal_reminder", "failure");
        run.Logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
    }

    [HumansFact]
    public async Task ReminderShutdown_RetainsEarlierDeliveryFailures()
    {
        using var shutdown = new CancellationTokenSource();
        var run = new ReminderRun();
        var failure = new IOException("Email enqueue failed");
        run.Email.SendAsync(Arg.Is<EmailMessage>(message => message.UserId == run.First.UserId), Arg.Any<CancellationToken>())
            .Returns(async call => { await shutdown.CancelAsync(); throw failure; });

        var error = await Xunit.Record.ExceptionAsync(() => run.Job.ExecuteAsync(shutdown.Token));

        error.Should().BeOfType<AggregateException>().Which.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(failure);
        await run.Email.DidNotReceive().SendAsync(Arg.Is<EmailMessage>(message => message.UserId == run.Next.UserId),
            Arg.Any<CancellationToken>());
        run.Metrics.Received(1).RecordJobRun("term_renewal_reminder", "failure");
        run.Metrics.DidNotReceive().RecordJobRun("term_renewal_reminder", "success");
    }

    private sealed class ReminderRun
    {
        public IGovernanceManagement Applications { get; } = Substitute.For<IGovernanceManagement>();
        public IUserService Users { get; } = Substitute.For<IUserService>();
        public IEmailService Email { get; } = Substitute.For<IEmailService>();
        public INotificationEmitter Notices { get; } = Substitute.For<INotificationEmitter>();
        public IHumansMetrics Metrics { get; } = Substitute.For<IHumansMetrics>();
        public CapturingLogger<TermRenewalReminderJob> Logger { get; } = new();
        public Instant Now { get; } = Instant.FromUtc(2026, 3, 1, 12, 0);
        public ApplicationRenewalReminderCandidate First { get; }
        public ApplicationRenewalReminderCandidate Next { get; }
        public TermRenewalReminderJob Job { get; }

        public ReminderRun()
        {
            First = new(Guid.NewGuid(), Guid.NewGuid(), MembershipTier.Colaborador, Now, new LocalDate(2026, 5, 20));
            Next = First with { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
            Applications.GetExpiringApplicationsNeedingReminderAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ApplicationRenewalReminderCandidate>>([First, Next]));
            Applications.GetPendingApplicationUserTiersAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<(Guid UserId, MembershipTier Tier)>>(new HashSet<(Guid, MembershipTier)>()));
            Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, UserInfo>
                {
                    [First.UserId] = new User { Id = First.UserId, BurnerName = "First", Email = "first@example.com" }.ToUserInfo(),
                    [Next.UserId] = new User { Id = Next.UserId, BurnerName = "Next", Email = "next@example.com" }.ToUserInfo()
                });
            Job = new(Applications, Users, Email, TestGovernanceEmails.Create(), Notices, Metrics, Logger, new FakeClock(Now));
        }
    }

    [HumansFact]
    public async Task ExecuteAsync_RoutesMergedApplicantsNoticeToTheLiveRecipient()
    {
        var applications = Substitute.For<IGovernanceManagement>();
        var users = Substitute.For<IUserService>();
        var email = Substitute.For<IEmailService>();
        var notifications = Substitute.For<INotificationEmitter>();
        var clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var candidate = new ApplicationRenewalReminderCandidate(Guid.NewGuid(), Guid.NewGuid(),
            MembershipTier.Colaborador, clock.GetCurrentInstant(), new LocalDate(2026, 5, 20));
        var liveId = Guid.NewGuid();
        applications.GetExpiringApplicationsNeedingReminderAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApplicationRenewalReminderCandidate>>([candidate]));
        applications.GetPendingApplicationUserTiersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<(Guid UserId, MembershipTier Tier)>>(new HashSet<(Guid, MembershipTier)>()));
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [candidate.UserId] = new User { Id = liveId, BurnerName = "Member", Email = "member@example.org" }.ToUserInfo()
            });
        var job = new TermRenewalReminderJob(applications, users, email, TestGovernanceEmails.Create(),
            notifications, Substitute.For<IHumansMetrics>(), NullLogger<TermRenewalReminderJob>.Instance, clock);

        await job.ExecuteAsync(CancellationToken.None);

        await applications.Received(1).GetExpiringApplicationsNeedingReminderAsync(
            new LocalDate(2026, 3, 1), new LocalDate(2026, 5, 30), CancellationToken.None);
        var notice = notifications.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        notice[0].Should().Be(GovernanceNotificationSources.TermRenewalReminder);
        ((IReadOnlyList<Guid>)notice[4]!).Should().ContainSingle().Which.Should().Be(liveId);
        await email.Received(1).SendAsync(Arg.Is<EmailMessage>(message => message.UserId == liveId), CancellationToken.None);
        await applications.Received(1).MarkRenewalReminderSentAsync(candidate.Id, clock.GetCurrentInstant(), CancellationToken.None);
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    [Xunit.InlineData("\t")]
    public async Task ExecuteAsync_UnreachableRecipientStaysEligibleForRetry(string? address)
    {
        var applications = Substitute.For<IGovernanceManagement>();
        var users = Substitute.For<IUserService>();
        var email = Substitute.For<IEmailService>();
        var notifications = Substitute.For<INotificationEmitter>();
        var clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var candidate = new ApplicationRenewalReminderCandidate(Guid.NewGuid(), Guid.NewGuid(),
            MembershipTier.Colaborador, clock.GetCurrentInstant(), new LocalDate(2026, 5, 20));
        applications.GetExpiringApplicationsNeedingReminderAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApplicationRenewalReminderCandidate>>([candidate]));
        applications.GetPendingApplicationUserTiersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<(Guid UserId, MembershipTier Tier)>>(new HashSet<(Guid, MembershipTier)>()));
        var member = new User { Id = candidate.UserId, BurnerName = "Member", Email = address }.ToUserInfo();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [candidate.UserId] = member });
        var job = new TermRenewalReminderJob(applications, users, email, TestGovernanceEmails.Create(),
            notifications, Substitute.For<IHumansMetrics>(), NullLogger<TermRenewalReminderJob>.Instance, clock);

        await job.ExecuteAsync(CancellationToken.None);

        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await applications.DidNotReceive().MarkRenewalReminderSentAsync(
            candidate.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [candidate.UserId] = member with { IdentityEmailColumn = "member@example.org" } });
        await job.ExecuteAsync(CancellationToken.None);

        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(message => message.RecipientEmail == "member@example.org"), CancellationToken.None);
        await applications.Received(1).MarkRenewalReminderSentAsync(candidate.Id, clock.GetCurrentInstant(), CancellationToken.None);
    }

    [HumansTheory]
    [Xunit.InlineData("email", true)]
    [Xunit.InlineData("stamp", true)]
    [Xunit.InlineData("notice", false)]
    public async Task ExecuteAsync_ReportsRequiredStepFailuresAfterAttemptingOtherRecipients(string failedStep, bool shouldFail)
    {
        var applications = Substitute.For<IGovernanceManagement>();
        var users = Substitute.For<IUserService>();
        var email = Substitute.For<IEmailService>();
        var notifications = Substitute.For<INotificationEmitter>();
        var metrics = Substitute.For<IHumansMetrics>();
        var clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var first = new ApplicationRenewalReminderCandidate(Guid.NewGuid(), Guid.NewGuid(),
            MembershipTier.Colaborador, clock.GetCurrentInstant(), new LocalDate(2026, 5, 20));
        var next = first with { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        applications.GetExpiringApplicationsNeedingReminderAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApplicationRenewalReminderCandidate>>([first, next]));
        applications.GetPendingApplicationUserTiersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<(Guid UserId, MembershipTier Tier)>>(new HashSet<(Guid, MembershipTier)>()));
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>
            {
                [first.UserId] = new User { Id = first.UserId, BurnerName = "First", Email = "first@example.com" }.ToUserInfo(),
                [next.UserId] = new User { Id = next.UserId, BurnerName = "Next", Email = "next@example.com" }.ToUserInfo()
            }));
        var failure = new IOException("Reminder dependency unavailable");
        if (string.Equals(failedStep, "email", StringComparison.Ordinal))
            email.SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == "first@example.com"), CancellationToken.None)
                .Returns(Task.FromException(failure));
        if (string.Equals(failedStep, "stamp", StringComparison.Ordinal))
            applications.MarkRenewalReminderSentAsync(first.Id, clock.GetCurrentInstant(), CancellationToken.None)
                .Returns(Task.FromException(failure), Task.CompletedTask);
        if (string.Equals(failedStep, "notice", StringComparison.Ordinal))
            notifications.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
                    Arg.Any<NotificationPriority>(), Arg.Any<string>(),
                    Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(first.UserId)),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    CancellationToken.None)
                .Returns(Task.FromException(failure));
        var job = new TermRenewalReminderJob(applications, users, email, TestGovernanceEmails.Create(),
            notifications, metrics, NullLogger<TermRenewalReminderJob>.Instance, clock);

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(CancellationToken.None));

        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "next@example.com" && m.DeliveryId == next.Id), CancellationToken.None);
        await applications.Received(1).MarkRenewalReminderSentAsync(next.Id, clock.GetCurrentInstant(), CancellationToken.None);
        if (string.Equals(failedStep, "email", StringComparison.Ordinal))
            await applications.DidNotReceive().MarkRenewalReminderSentAsync(first.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        if (shouldFail)
        {
            error.Should().BeOfType<AggregateException>().Subject.InnerExceptions
                .Should().ContainSingle().Which.Should().BeSameAs(failure);
            metrics.Received(1).RecordJobRun("term_renewal_reminder", "failure");
            metrics.DidNotReceive().RecordJobRun("term_renewal_reminder", "success");
        }
        else
        {
            error.Should().BeNull();
            metrics.Received(1).RecordJobRun("term_renewal_reminder", "success");
        }

        if (string.Equals(failedStep, "stamp", StringComparison.Ordinal))
        {
            applications.GetExpiringApplicationsNeedingReminderAsync(
                    Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ApplicationRenewalReminderCandidate>>([first]));

            await job.ExecuteAsync(CancellationToken.None);

            await email.Received(2).SendAsync(
                Arg.Is<EmailMessage>(message => message.RecipientEmail == "first@example.com" && message.DeliveryId == first.Id),
                CancellationToken.None);
            await email.Received(1).SendAsync(
                Arg.Is<EmailMessage>(message => message.RecipientEmail == "next@example.com"), CancellationToken.None);
        }
    }

    [HumansTheory]
    [Xunit.InlineData("es", "es")]
    [Xunit.InlineData("unsupported", "en")]
    [Xunit.InlineData("", "en")]
    [Xunit.InlineData(" ", "en")]
    [Xunit.InlineData("not a culture!", "en")]
    [Xunit.InlineData("fr-FR", "en")]
    public async Task ExecuteAsync_LocalizesNoticeAndDateWithoutLeakingRecipientCulture(string language, string expectedCulture)
    {
        using var serverCulture = new CultureScope("fr");
        var applications = Substitute.For<IGovernanceManagement>();
        var users = Substitute.For<IUserService>();
        var email = Substitute.For<IEmailService>();
        var notifications = Substitute.For<INotificationEmitter>();
        var clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var userId = Guid.NewGuid();
        var expiry = new LocalDate(2026, 5, 20);
        var candidate = new ApplicationRenewalReminderCandidate(Guid.NewGuid(), userId,
            MembershipTier.Colaborador, clock.GetCurrentInstant(), expiry);
        applications.GetExpiringApplicationsNeedingReminderAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApplicationRenewalReminderCandidate>>([candidate]));
        applications.GetPendingApplicationUserTiersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<(Guid UserId, MembershipTier Tier)>>(new HashSet<(Guid, MembershipTier)>()));
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>
            {
                [userId] = new User { Id = userId, BurnerName = "Applicant", Email = "applicant@example.com", PreferredLanguage = language }.ToUserInfo()
            }));
        var job = new TermRenewalReminderJob(applications, users, email, TestGovernanceEmails.Create(),
            notifications, Substitute.For<IHumansMetrics>(), NullLogger<TermRenewalReminderJob>.Instance, clock);
        string expectedDate;
        using (new CultureScope(expectedCulture))
            expectedDate = expiry.ToDate();

        await job.ExecuteAsync(CancellationToken.None);

        var args = notifications.ReceivedCalls().Single().GetArguments();
        args[0].Should().Be(GovernanceNotificationSources.TermRenewalReminder);
        ((IReadOnlyList<Guid>)args[4]!).Should().ContainSingle().Which.Should().Be(userId);
        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "applicant@example.com"
                && m.Subject.EndsWith("#" + expectedCulture, StringComparison.Ordinal)), CancellationToken.None);
        var recipientCulture = CultureInfo.GetCultureInfo(expectedCulture);
        var resources = new System.Resources.ResourceManager(typeof(GovernanceResource));
        args[3].Should().Be(string.Format(recipientCulture,
            resources.GetString("Governance_Notification_TermRenewalReminder", recipientCulture)!, MembershipTier.Colaborador, expectedDate));
        args[5].Should().Be(resources.GetString("Governance_Notification_TermRenewalReminderBody", recipientCulture));
        args[6].Should().Be("/Governance/Applications");
        args[7].Should().Be(resources.GetString("Governance_Notification_Renew", recipientCulture));
        await applications.Received(1).MarkRenewalReminderSentAsync(candidate.Id, clock.GetCurrentInstant(), CancellationToken.None);
        CultureInfo.CurrentCulture.Name.Should().Be("fr");
        CultureInfo.CurrentUICulture.Name.Should().Be("fr");
    }
}
