using Humans.Consent.Services;
using Microsoft.Extensions.Localization;
using Humans.Base.Extensions;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Consent.Jobs;
using Humans.Consent.Tests.Infrastructure;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

public sealed class SendReConsentReminderJobTests
{
    [HumansTheory]
    [InlineData("send", "en", "Reminder: 3 days to review updated documents")]
    [InlineData("stamp", "en", "Reminder: 3 days to review updated documents")]
    [InlineData("success", "en", "Reminder: 3 days to review updated documents")]
    [InlineData("success", "es", "Recordatorio: 3 días para revisar documentos actualizados")]
    [InlineData("success", "", "Reminder: 3 days to review updated documents")]
    [InlineData("success", " ", "Reminder: 3 days to review updated documents")]
    [InlineData("success", "not a culture!", "Reminder: 3 days to review updated documents")]
    [InlineData("success", "fr-FR", "Reminder: 3 days to review updated documents")]
    public async Task RecipientFailure_DoesNotPreventLaterRemindersOrHideJobFailure(string outcome, string language, string expectedSubject)
    {
        using var serverCulture = new CultureScope("fr");
        var now = Instant.FromUtc(2026, 10, 2, 4, 0);
        var first = MakeUser("first@example.com") with { PreferredLanguage = language };
        var next = MakeUser("next@example.com");
        var cooling = MakeUser("cooling@example.com", now);
        var membership = Substitute.For<IMembershipCalculatorRead>();
        membership.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>(), Arg.Any<Duration?>())
            .Returns(new[] { first.Id, next.Id, cooling.Id });
        var legal = Substitute.For<ILegalDocumentSyncServiceRead>();
        legal.GetRequiredVersionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var users = Substitute.For<IUserService>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [first.Id] = first,
                [next.Id] = next,
                [cooling.Id] = cooling
            });
        var email = Substitute.For<IEmailService>();
        var failure = new IOException("Recipient failure");
        if (string.Equals(outcome, "send", StringComparison.Ordinal))
            email.SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == first.Email), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(failure));
        if (string.Equals(outcome, "stamp", StringComparison.Ordinal))
            users.SetLastConsentReminderSentAsync(first.Id, now, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(failure));
        var metrics = Substitute.For<IHumansMetrics>();
        var emailMessages = new ConsentEmails(
            Options.Create(new EmailSettings { BaseUrl = TestConsentEmails.BaseUrl }),
            new StringLocalizer<ConsentResource>(new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)),
            NullLogger<ConsentEmails>.Instance);
        var job = new SendReConsentReminderJob(membership, legal, users, email,
            emailMessages, Options.Create(new EmailSettings { ConsentReminderCooldownDays = 7, ConsentReminderDaysBeforeSuspension = 3 }),
            metrics, NullLogger<SendReConsentReminderJob>.Instance, new FakeClock(now));

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(TestContext.Current.CancellationToken));

        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == first.Email
                && m.Subject == expectedSubject), Arg.Any<CancellationToken>());
        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == next.Email), Arg.Any<CancellationToken>());
        await membership.Received(1).GetUsersRequiringStatusUpdateAsync(
            TestContext.Current.CancellationToken, Duration.FromDays(3));
        await users.Received(1).SetLastConsentReminderSentAsync(next.Id, now, Arg.Any<CancellationToken>());
        await email.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == cooling.Email), Arg.Any<CancellationToken>());
        if (string.Equals(outcome, "success", StringComparison.Ordinal))
        {
            error.Should().BeNull();
            metrics.Received(1).RecordJobRun("send_reconsent_reminder", "success");
        }
        else
        {
            error.Should().BeOfType<AggregateException>().Subject.InnerExceptions
                .Should().ContainSingle().Which.Should().BeSameAs(failure);
            metrics.Received(1).RecordJobRun("send_reconsent_reminder", "failure");
            metrics.DidNotReceive().RecordJobRun("send_reconsent_reminder", "success");
        }
        if (string.Equals(outcome, "send", StringComparison.Ordinal))
            await users.DidNotReceive().SetLastConsentReminderSentAsync(first.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Cancellation_StopsFanoutInsteadOfAggregating()
    {
        var now = Instant.FromUtc(2026, 10, 2, 4, 0);
        var first = MakeUser("first@example.com");
        var next = MakeUser("next@example.com");
        var membership = Substitute.For<IMembershipCalculatorRead>();
        membership.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>(), Arg.Any<Duration?>())
            .Returns(new[] { first.Id, next.Id });
        var legal = Substitute.For<ILegalDocumentSyncServiceRead>();
        legal.GetRequiredVersionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var users = Substitute.For<IUserService>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [first.Id] = first, [next.Id] = next });
        var email = Substitute.For<IEmailService>();
        email.SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == first.Email), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));
        var job = new SendReConsentReminderJob(membership, legal, users, email,
            TestConsentEmails.Create(), Options.Create(new EmailSettings { ConsentReminderCooldownDays = 7 }),
            Substitute.For<IHumansMetrics>(), NullLogger<SendReConsentReminderJob>.Instance, new FakeClock(now));

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(TestContext.Current.CancellationToken));

        error.Should().BeOfType<OperationCanceledException>();
        await email.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == next.Email), Arg.Any<CancellationToken>());
    }

    private static UserInfo MakeUser(string email, Instant? sentAt = null)
    {
        var user = new User { Id = Guid.NewGuid(), BurnerName = "Member", LastConsentReminderSentAt = sentAt };
        return UserInfo.Create(user,
            [new UserEmail { Id = Guid.NewGuid(), UserId = user.Id, Email = email, IsVerified = true, IsPrimary = true }],
            [], [], null, []);
    }
}
