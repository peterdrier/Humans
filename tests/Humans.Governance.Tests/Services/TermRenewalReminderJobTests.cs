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
using Humans.Base.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Governance.Tests.Services;

public sealed class TermRenewalReminderJobTests
{
    [HumansTheory]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("unsupported")]
    public async Task ExecuteAsync_LocalizesNoticeAndDateWithoutLeakingRecipientCulture(string language)
    {
        using var serverCulture = new CultureScope("fr");
        var applications = Substitute.For<IApplicationDecisionService>();
        var users = Substitute.For<IUserServiceRead>();
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
        args[0].Should().Be(NotificationSource.TermRenewalReminder);
        ((IReadOnlyList<Guid>)args[4]!).Should().ContainSingle().Which.Should().Be(userId);
        var spanish = string.Equals(language, "es", StringComparison.Ordinal);
        args[3].Should().Be(spanish ? $"Tu período como Colaborador vence el {expectedDate}" : $"Your Colaborador term expires {expectedDate}");
        args[5].Should().Be(spanish ? "Presenta una solicitud de renovación para mantener tu categoría de membresía." : "Submit a renewal application to maintain your membership tier.");
        args[6].Should().Be("/Governance/Applications");
        args[7].Should().Be(spanish ? "Renovar →" : "Renew →");
        await applications.Received(1).MarkRenewalReminderSentAsync(candidate.Id, clock.GetCurrentInstant(), CancellationToken.None);
        CultureInfo.CurrentCulture.Name.Should().Be("fr");
        CultureInfo.CurrentUICulture.Name.Should().Be("fr");
    }
}
