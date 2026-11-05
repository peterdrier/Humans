using Humans.Consent.Services;
using Microsoft.Extensions.Localization;
using Humans.Base.Extensions;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
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
    [InlineData("send", "en", "en")]
    [InlineData("stamp", "en", "en")]
    [InlineData("success", "en", "en")]
    [InlineData("success", "es", "es")]
    [InlineData("success", "", "en")]
    [InlineData("success", " ", "en")]
    [InlineData("success", "not a culture!", "en")]
    [InlineData("success", "fr-FR", "en")]
    public async Task RecipientFailure_DoesNotPreventLaterRemindersOrHideJobFailure(string outcome, string language, string expectedLanguage)
    {
        using var serverCulture = new CultureScope("fr");
        var now = Instant.FromUtc(2026, 10, 2, 4, 0);
        var first = MakeUser("first@example.com") with { PreferredLanguage = language };
        var next = MakeUser("next@example.com");
        var cooling = MakeUser("cooling@example.com");
        var membership = Substitute.For<IMembershipCalculator>();
        membership.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>(), Arg.Any<Duration?>())
            .Returns(new[] { first.Id, next.Id, cooling.Id });
        var legal = Substitute.For<ILegalDocumentSyncManagement>();
        legal.GetRequiredVersionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var users = Substitute.For<IUserService>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [first.Id] = first,
                [next.Id] = next,
                [cooling.Id] = cooling
            });
        var reminderRepo = Substitute.For<IConsentReminderRepository>();
        reminderRepo.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new ConsentReminder { UserId = cooling.Id, LastSentAt = now }]);
        var email = Substitute.For<IEmailService>();
        var failure = new IOException("Recipient failure");
        if (string.Equals(outcome, "send", StringComparison.Ordinal))
            email.SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == first.Email), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(failure));
        if (string.Equals(outcome, "stamp", StringComparison.Ordinal))
            reminderRepo.UpsertAsync(first.Id, now, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(failure));
        var metrics = Substitute.For<IHumansMetrics>();
        var emailMessages = new ConsentEmails(
            Options.Create(new EmailSettings { BaseUrl = TestConsentEmails.BaseUrl }),
            new StringLocalizer<ConsentResource>(new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)),
            NullLogger<ConsentEmails>.Instance);
        var job = new SendReConsentReminderJob(membership, legal, users, new ConsentReminderService(reminderRepo, users), email,
            emailMessages, Options.Create(new EmailSettings { ConsentReminderCooldownDays = 7, ConsentReminderDaysBeforeSuspension = 3 }),
            metrics, NullLogger<SendReConsentReminderJob>.Instance, new FakeClock(now));

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(TestContext.Current.CancellationToken));
        var expectedCulture = System.Globalization.CultureInfo.GetCultureInfo(expectedLanguage);
        var expectedSubject = string.Format(expectedCulture, new System.Resources.ResourceManager(typeof(ConsentResource))
            .GetString("Consent_Email_ReConsentReminder_Subject", expectedCulture)!, 3);

        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == first.Email
                && m.Subject == expectedSubject), Arg.Any<CancellationToken>());
        await email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == next.Email), Arg.Any<CancellationToken>());
        await membership.Received(1).GetUsersRequiringStatusUpdateAsync(
            TestContext.Current.CancellationToken, Duration.FromDays(3));
        await reminderRepo.Received(1).UpsertAsync(next.Id, now, Arg.Any<CancellationToken>());
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
            await reminderRepo.DidNotReceive().UpsertAsync(first.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StampFailure_RetryReusesDeliveryId_AndNextCooldownCycleGetsANewOne(bool previouslyReminded)
    {
        var now = Instant.FromUtc(2026, 10, 2, 4, 0);
        var clock = new FakeClock(now);
        var member = MakeUser("member@example.com");
        var membership = Substitute.For<IMembershipCalculator>();
        membership.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>(), Arg.Any<Duration?>())
            .Returns(new[] { member.Id });
        var legal = Substitute.For<ILegalDocumentSyncManagement>();
        legal.GetRequiredVersionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var users = Substitute.For<IUserService>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [member.Id] = member });
        var stamps = new List<ConsentReminder>();
        if (previouslyReminded)
            stamps.Add(new ConsentReminder { UserId = member.Id, LastSentAt = now - Duration.FromDays(14) });
        var repo = Substitute.For<IConsentReminderRepository>();
        repo.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => stamps.ToList());
        var failStamp = true;
        repo.UpsertAsync(member.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (failStamp)
                {
                    failStamp = false;
                    return Task.FromException(new IOException("Stamp unavailable"));
                }
                stamps.Clear();
                stamps.Add(new ConsentReminder { UserId = member.Id, LastSentAt = call.Arg<Instant>() });
                return Task.CompletedTask;
            });
        var deliveries = new List<EmailMessage>();
        var email = Substitute.For<IEmailService>();
        email.When(sender => sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()))
            .Do(call => deliveries.Add(call.Arg<EmailMessage>()));
        var job = new SendReConsentReminderJob(membership, legal, users, new ConsentReminderService(repo, users), email,
            TestConsentEmails.Create(), Options.Create(new EmailSettings { ConsentReminderCooldownDays = 7 }),
            Substitute.For<IHumansMetrics>(), NullLogger<SendReConsentReminderJob>.Instance, clock);
        var action = () => job.ExecuteAsync(TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<AggregateException>();
        await action();
        await action();

        deliveries.Should().HaveCount(2);
        deliveries[0].DeliveryId.Should().NotBeNull();
        deliveries[1].DeliveryId.Should().Be(deliveries[0].DeliveryId);

        clock.Advance(Duration.FromDays(8));
        await action();

        deliveries.Should().HaveCount(3);
        deliveries[2].DeliveryId.Should().NotBeNull().And.NotBe(deliveries[0].DeliveryId!.Value);
    }

    [HumansFact]
    public async Task Cancellation_StopsFanoutInsteadOfAggregating()
    {
        var now = Instant.FromUtc(2026, 10, 2, 4, 0);
        var first = MakeUser("first@example.com");
        var next = MakeUser("next@example.com");
        var membership = Substitute.For<IMembershipCalculator>();
        membership.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>(), Arg.Any<Duration?>())
            .Returns(new[] { first.Id, next.Id });
        var legal = Substitute.For<ILegalDocumentSyncManagement>();
        legal.GetRequiredVersionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var users = Substitute.For<IUserService>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [first.Id] = first, [next.Id] = next });
        var reminderRepo = Substitute.For<IConsentReminderRepository>();
        reminderRepo.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var email = Substitute.For<IEmailService>();
        email.SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == first.Email), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));
        var job = new SendReConsentReminderJob(membership, legal, users, new ConsentReminderService(reminderRepo, users), email,
            TestConsentEmails.Create(), Options.Create(new EmailSettings { ConsentReminderCooldownDays = 7 }),
            Substitute.For<IHumansMetrics>(), NullLogger<SendReConsentReminderJob>.Instance, new FakeClock(now));

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(TestContext.Current.CancellationToken));

        error.Should().BeOfType<OperationCanceledException>();
        await email.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == next.Email), Arg.Any<CancellationToken>());
    }

    private static UserInfo MakeUser(string email)
    {
        var user = new User { Id = Guid.NewGuid(), BurnerName = "Member" };
        return UserInfo.Create(user,
            [new UserEmail { Id = Guid.NewGuid(), UserId = user.Id, Email = email, IsVerified = true, IsPrimary = true }], [], null);
    }
}
