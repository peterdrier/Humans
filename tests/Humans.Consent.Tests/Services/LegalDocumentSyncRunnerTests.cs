using System.Globalization;
using System.Resources;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Consent.Data;
using Humans.Consent.Contracts;
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
    [InlineData(false, "es", "es", "member@example.com", UserState.Active, true)]
    [InlineData(false, null, "en", "member@example.com", UserState.Active, true)]
    [InlineData(false, "", "en", "member@example.com", UserState.Active, true)]
    [InlineData(false, "pt", "en", "member@example.com", UserState.Active, true)]
    [InlineData(false, "fr-FR", "en", "member@example.com", UserState.Active, true)]
    [InlineData(false, "not a culture!", "en", "member@example.com", UserState.Active, true)]
    [InlineData(true, "en", "en", "member@example.com", UserState.Active, true)]
    [InlineData(false, "es", "es", "", UserState.Active, true)]
    [InlineData(false, "es", "es", " \t ", UserState.Active, true)]
    [InlineData(false, "es", "es", "member@example.com", UserState.Rejected, true)]
    [InlineData(false, "es", "es", "member@example.com", UserState.Deleted, true)]
    [InlineData(false, "es", "es", "member@example.com", UserState.Merged, true)]
    [InlineData(false, "es", "es", "member@example.com", UserState.Bare, false)]
    [InlineData(false, "es", "es", "member@example.com", UserState.Suspended, true)]
    [InlineData(false, "es", "es", "member@example.com", UserState.AdminSuspended, true)]
    public async Task Sync_NotifiesOnlyForOutstandingRequiredDocumentsInMembersTeams(
        bool alreadyConsented, string? language, string expectedLanguage, string recipientEmail, UserState state, bool hasProfile)
    {
        using var culture = new CultureScope("fr");
        var member = new User { Id = Guid.NewGuid(), BurnerName = "Member", PreferredLanguage = language!, State = state };
        var user = UserInfo.Create(member,
            [new UserEmail { Id = Guid.NewGuid(), UserId = member.Id, Email = recipientEmail, IsVerified = true, IsPrimary = true }], [], hasProfile ? Humans.Testing.UserFixtures.Profile() : null);
        var own = MakeDocument("Own required", true);
        var other = MakeDocument("Other required", true);
        var optional = MakeDocument("Own optional", false);
        optional.TeamId = own.TeamId;
        var sync = Substitute.For<ILegalDocumentSyncManagement>();
        sync.SyncAllDocumentsAsync(Arg.Any<CancellationToken>(), Arg.Any<ICollection<Exception>?>()).Returns(new[] { own, other, optional });
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamAsync(own.TeamId, Arg.Any<CancellationToken>()).Returns(new TeamInfo(
            own.TeamId, "Own", null, "own", true, false, SystemTeamType.None, false,
            false, false, false, default,
            [new TeamMemberInfo(Guid.NewGuid(), user.Id, "Member", user.Email, null, TeamMemberRole.Member, default)]));
        var users = Substitute.For<IUserService>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [user.Id] = user });
        var repository = Substitute.For<ILegalDocumentRepository>();
        repository.GetDocumentsAsync(null, Arg.Any<CancellationToken>()).Returns(new[] { own, other, optional });
        var consents = Substitute.For<IConsentService>();
        consents.GetConsentMapForUsersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlySet<Guid>> { [user.Id] = alreadyConsented ? new HashSet<Guid> { own.CurrentVersion!.Id } : new HashSet<Guid>() });
        var email = Substitute.For<IEmailService>();
        var localizerFactory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var emailMessages = new ConsentEmails(
            Options.Create(new EmailSettings { BaseUrl = TestConsentEmails.BaseUrl }),
            new StringLocalizer<ConsentResource>(localizerFactory), NullLogger<ConsentEmails>.Instance);
        var runner = new LegalDocumentSyncRunner(sync, email, emailMessages, teams, users,
            consents, repository, NullLogger<LegalDocumentSyncRunner>.Instance);

        await runner.SyncAndNotifyAsync(TestContext.Current.CancellationToken);

        var messages = email.ReceivedCalls().Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IEmailService.SendAsync), StringComparison.Ordinal))
            .Select(c => (EmailMessage)c.GetArguments()[0]!).ToList();
        if (alreadyConsented || string.IsNullOrWhiteSpace(recipientEmail) || !user.IsActive)
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
        var sync = Substitute.For<ILegalDocumentSyncManagement>();
        sync.SyncAllDocumentsAsync(Arg.Any<CancellationToken>(), Arg.Any<ICollection<Exception>?>()).Returns(new[] { MakeDocument("Optional", false) });
        var teams = Substitute.For<ITeamService>();
        var email = Substitute.For<IEmailService>();
        var runner = new LegalDocumentSyncRunner(sync, email, TestConsentEmails.Create(), teams,
            Substitute.For<IUserService>(), Substitute.For<IConsentService>(), Substitute.For<ILegalDocumentRepository>(),
            NullLogger<LegalDocumentSyncRunner>.Instance);

        await runner.SyncAndNotifyAsync(TestContext.Current.CancellationToken);

        await teams.DidNotReceive().GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("legacy")]
    [InlineData("inactive")]
    [InlineData("optional")]
    [InlineData("superseded")]
    public async Task Sync_DoesNotAnnounceHistoricalOrNoLongerRequiredVersions(string mode)
    {
        var document = MakeDocument("Agreement", true);
        var batchId = document.CurrentVersion!.PendingReConsentBatchId!.Value;
        if (mode is "legacy") document.CurrentVersion.PendingReConsentBatchId = null;
        if (mode is "inactive") document.IsActive = false;
        if (mode is "optional") document.IsRequired = false;
        if (mode is "superseded") document.Versions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = document.Id,
            EffectiveFrom = document.CurrentVersion.EffectiveFrom + Duration.FromDays(1)
        });
        var sync = Substitute.For<ILegalDocumentSyncManagement>();
        sync.SyncAllDocumentsAsync(Arg.Any<CancellationToken>(), Arg.Any<ICollection<Exception>?>()).Returns(Array.Empty<LegalDocument>());
        var documents = Substitute.For<ILegalDocumentRepository>();
        documents.GetDocumentsAsync(null, Arg.Any<CancellationToken>()).Returns(new[] { document });
        var teams = Substitute.For<ITeamService>();
        var email = Substitute.For<IEmailService>();
        var runner = new LegalDocumentSyncRunner(sync, email, TestConsentEmails.Create(), teams,
            Substitute.For<IUserService>(), Substitute.For<IConsentService>(), documents,
            NullLogger<LegalDocumentSyncRunner>.Instance);

        await runner.SyncAndNotifyAsync(TestContext.Current.CancellationToken);

        await teams.DidNotReceive().GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        email.ReceivedCalls().Should().BeEmpty();
        if (mode is "legacy")
            await documents.DidNotReceive().CompleteReConsentBatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        else
            await documents.Received(1).CompleteReConsentBatchAsync(batchId, Arg.Any<CancellationToken>());
    }

    private static LegalDocument MakeDocument(string name, bool required)
    {
        var document = new LegalDocument { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Name = name, IsRequired = required };
        document.Versions.Add(new DocumentVersion { Id = Guid.NewGuid(), LegalDocumentId = document.Id, EffectiveFrom = Instant.FromUtc(2026, 10, 2, 1, 0), PendingReConsentBatchId = required ? Guid.NewGuid() : null });
        return document;
    }
}
