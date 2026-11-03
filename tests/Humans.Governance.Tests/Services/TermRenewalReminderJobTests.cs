using Humans.Governance.Services;
using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Jobs;
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
    [Xunit.InlineData("es")]
    [Xunit.InlineData("unsupported")]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    [Xunit.InlineData("not a culture!")]
    [Xunit.InlineData("fr-FR")]
    public async Task ExecuteAsync_LocalizesNoticeAndDateWithoutLeakingRecipientCulture(string language)
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
        using (new CultureScope(string.Equals(language, "es", StringComparison.Ordinal) ? "es" : "en"))
            expectedDate = expiry.ToDate();

        await job.ExecuteAsync(CancellationToken.None);

        var args = notifications.ReceivedCalls().Single().GetArguments();
        args[0].Should().Be(GovernanceNotificationSources.TermRenewalReminder);
        ((IReadOnlyList<Guid>)args[4]!).Should().ContainSingle().Which.Should().Be(userId);
        var spanish = string.Equals(language, "es", StringComparison.Ordinal);
        var expectedCulture = spanish ? "es" : "en";
        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "applicant@example.com"
                && m.Subject.EndsWith("#" + expectedCulture, StringComparison.Ordinal)), CancellationToken.None);
        args[3].Should().Be(spanish ? $"Tu período como Colaborador vence el {expectedDate}" : $"Your Colaborador term expires {expectedDate}");
        args[5].Should().Be(spanish ? "Presenta una solicitud de renovación para mantener tu categoría de membresía." : "Submit a renewal application to maintain your membership tier.");
        args[6].Should().Be("/Governance/Applications");
        args[7].Should().Be(spanish ? "Renovar →" : "Renew →");
        await applications.Received(1).MarkRenewalReminderSentAsync(candidate.Id, clock.GetCurrentInstant(), CancellationToken.None);
        CultureInfo.CurrentCulture.Name.Should().Be("fr");
        CultureInfo.CurrentUICulture.Name.Should().Be("fr");
    }
}
