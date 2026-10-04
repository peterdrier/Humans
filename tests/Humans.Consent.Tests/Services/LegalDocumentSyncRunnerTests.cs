using System.Globalization;
using System.Resources;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Consent.Services;
using Humans.Consent.Tests.Infrastructure;
using Humans.Email.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

public sealed class LegalDocumentSyncRunnerTests
{
    [HumansTheory]
    [InlineData(false, "es", "es")]
    [InlineData(false, null, "en")]
    [InlineData(false, "", "en")]
    [InlineData(false, "pt", "en")]
    [InlineData(false, "fr-FR", "en")]
    [InlineData(false, "not a culture!", "en")]
    [InlineData(true, "en", "en")]
    public async Task Sync_NotifiesOnlyForOutstandingRequiredDocumentsInMembersTeams(
        bool alreadyConsented, string? language, string expectedLanguage)
    {
        using var culture = new CultureScope("fr");
        var member = new User { Id = Guid.NewGuid(), BurnerName = "Member", PreferredLanguage = language! };
        var user = UserInfo.Create(member,
            [new UserEmail { Id = Guid.NewGuid(), UserId = member.Id, Email = "member@example.com", IsVerified = true, IsPrimary = true }],
            [], [], null, []);
        var own = MakeDocument("Own required", true);
        var other = MakeDocument("Other required", true);
        var optional = MakeDocument("Own optional", false);
        optional.TeamId = own.TeamId;
        var sync = Substitute.For<ILegalDocumentSyncService>();
        sync.SyncAllDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new[] { own, other, optional });
        var teams = Substitute.For<ITeamServiceRead>();
        teams.GetTeamAsync(own.TeamId, Arg.Any<CancellationToken>()).Returns(new TeamInfo(
            own.TeamId, "Own", null, "own", true, false, SystemTeamType.None, false,
            false, false, false, default,
            [new TeamMemberInfo(Guid.NewGuid(), user.Id, "Member", user.Email, null, TeamMemberRole.Member, default)]));
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [user.Id] = user });
        var repository = Substitute.For<IConsentRepository>();
        repository.GetPairsForUsersAndVersionsAsync(Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(alreadyConsented ? new[] { (user.Id, own.CurrentVersion!.Id) } : []);
        var email = Substitute.For<IEmailService>();
        var localizerFactory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var emailMessages = new ConsentEmails(
            Options.Create(new EmailSettings { BaseUrl = TestConsentEmails.BaseUrl }),
            new StringLocalizer<ConsentResource>(localizerFactory), NullLogger<ConsentEmails>.Instance);
        var runner = new LegalDocumentSyncRunner(sync, email, emailMessages, teams, users,
            repository, NullLogger<LegalDocumentSyncRunner>.Instance);

        await runner.SyncAndNotifyAsync(TestContext.Current.CancellationToken);

        var messages = email.ReceivedCalls().Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IEmailService.SendAsync), StringComparison.Ordinal))
            .Select(c => (EmailMessage)c.GetArguments()[0]!).ToList();
        if (alreadyConsented)
            messages.Should().BeEmpty();
        else
        {
            var message = messages.Should().ContainSingle().Subject;
            message.HtmlBody.Should().Contain("Own required")
                .And.NotContain("Other required").And.NotContain("Own optional");
            var recipientCulture = CultureInfo.GetCultureInfo(expectedLanguage);
            var resources = new ResourceManager(typeof(ConsentResource));
            message.Subject.Should().Be(string.Format(recipientCulture,
                resources.GetString("Consent_Email_ReConsentRequired_Subject_Single", recipientCulture)!, own.Name));
        }
        CultureInfo.CurrentUICulture.Name.Should().Be("fr");
    }

    [HumansFact]
    public async Task Sync_OptionalUpdate_DoesNotResolveRecipientsOrSendConsentEmail()
    {
        var sync = Substitute.For<ILegalDocumentSyncService>();
        sync.SyncAllDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new[] { MakeDocument("Optional", false) });
        var teams = Substitute.For<ITeamServiceRead>();
        var email = Substitute.For<IEmailService>();
        var runner = new LegalDocumentSyncRunner(sync, email, TestConsentEmails.Create(), teams,
            Substitute.For<IUserServiceRead>(), Substitute.For<IConsentRepository>(),
            NullLogger<LegalDocumentSyncRunner>.Instance);

        await runner.SyncAndNotifyAsync(TestContext.Current.CancellationToken);

        await teams.DidNotReceive().GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    private static LegalDocument MakeDocument(string name, bool required)
    {
        var document = new LegalDocument { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Name = name, IsRequired = required };
        document.Versions.Add(new DocumentVersion { Id = Guid.NewGuid(), LegalDocumentId = document.Id, EffectiveFrom = Instant.FromUtc(2026, 10, 2, 1, 0) });
        return document;
    }
}
