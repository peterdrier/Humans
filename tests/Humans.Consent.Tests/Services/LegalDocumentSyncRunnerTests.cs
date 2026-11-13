using Humans.Consent.Jobs;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Logging;
using NodaTime.Testing;
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
    [InlineData("Shutdown")]
    [InlineData("Dependency")]
    [InlineData("Fault")]
    public async Task ConsentShutdown_SyncJobDistinguishesCancellationFromFaults(string kind)
    {
        using var shutdown = new CancellationTokenSource();
        var failure = kind is "Fault" ? (Exception)new IOException("Sync failed")
            : new OperationCanceledException(kind is "Shutdown" ? shutdown.Token : new CancellationToken(true));
        var runner = Substitute.For<ILegalDocumentSyncRunner>();
        runner.SyncAndNotifyAsync(shutdown.Token).Returns(async _ =>
        { if (kind is "Shutdown") await shutdown.CancelAsync(); throw failure; });
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<SyncLegalDocumentsJob>();
        var job = new SyncLegalDocumentsJob(runner, metrics, logger, new FakeClock(Instant.FromUtc(2026, 10, 2, 4, 0)));

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        metrics.DidNotReceive().RecordJobRun("sync_legal_documents", "success");
        if (kind is "Shutdown")
        {
            metrics.DidNotReceive().RecordJobRun("sync_legal_documents", "failure");
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
            logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        }
        else metrics.Received(1).RecordJobRun("sync_legal_documents", "failure");
    }

    [HumansFact]
    public async Task ConsentShutdown_PendingBatchRetriesWithStableDeliveryIds()
    {
        using var shutdown = new CancellationTokenSource();
        var run = new SyncRun();
        var deliveries = new List<EmailMessage>();
        run.Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            deliveries.Add(call.Arg<EmailMessage>());
            await shutdown.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
        });

        var error = await Record.ExceptionAsync(() => run.Runner.SyncAndNotifyAsync(shutdown.Token));

        error.Should().BeAssignableTo<OperationCanceledException>();
        deliveries.Should().ContainSingle();
        await run.Email.Received(1).SendAsync(Arg.Any<EmailMessage>(), CancellationToken.None);
        await run.Documents.DidNotReceiveWithAnyArgs().CompleteReConsentBatchAsync(default);
        var firstId = deliveries[0].DeliveryId;
        await run.Runner.SyncAndNotifyAsync(CancellationToken.None);
        deliveries.Should().HaveCount(3);
        deliveries[1].DeliveryId.Should().Be(firstId);
        deliveries.Select(m => m.DeliveryId).Distinct().Should().HaveCount(2);
        await run.Documents.Received(1).CompleteReConsentBatchAsync(run.BatchId, CancellationToken.None);
    }

    [HumansFact]
    public async Task ConsentShutdown_FinishedBatchCommitsCompletionWithoutTheStoppedToken()
    {
        using var shutdown = new CancellationTokenSource();
        var run = new SyncRun(singleRecipient: true);
        run.Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await shutdown.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
        });
        run.Documents.CompleteReConsentBatchAsync(run.BatchId, Arg.Any<CancellationToken>()).Returns(call =>
        { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await run.Runner.SyncAndNotifyAsync(shutdown.Token);

        await run.Documents.Received(1).CompleteReConsentBatchAsync(run.BatchId, CancellationToken.None);
    }

    [HumansTheory]
    [InlineData("Publication")]
    [InlineData("Team")]
    [InlineData("Email")]
    [InlineData("Completion")]
    public async Task ConsentShutdown_PreservesFailuresCollectedBeforeCancellation(string boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var run = new SyncRun(singleRecipient: boundary is "Completion");
        Exception failure = boundary is "Completion" ? new OperationCanceledException("Completion dependency stopped")
            : new IOException("Document dependency failed");
        if (boundary is "Publication")
            run.Sync.SyncAllDocumentsAsync(shutdown.Token, Arg.Any<ICollection<Exception>?>())
                .Returns(async Task<IReadOnlyList<LegalDocument>> (call) =>
                {
                    call.Arg<ICollection<Exception>>().Add(failure);
                    await shutdown.CancelAsync();
                    throw new OperationCanceledException(shutdown.Token);
                });
        else
        {
            if (boundary is "Team")
            {
                var broken = MakeDocument("Broken team", true);
                broken.CurrentVersion!.PendingReConsentBatchId = run.BatchId;
                run.Documents.GetDocumentsAsync(null, Arg.Any<CancellationToken>()).Returns(new[] { broken, run.Document });
                run.Teams.GetTeamAsync(broken.TeamId, Arg.Any<CancellationToken>()).Returns(Task.FromException<TeamInfo?>(failure));
            }
            run.Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await shutdown.CancelAsync();
                if (boundary is "Email") throw failure;
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            });
            if (boundary is "Completion")
                run.Documents.CompleteReConsentBatchAsync(run.BatchId, CancellationToken.None).Returns(Task.FromException(failure));
        }

        var error = await Record.ExceptionAsync(() => run.Runner.SyncAndNotifyAsync(shutdown.Token));

        error.Should().BeOfType<AggregateException>().Which.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(failure);
        if (boundary is "Completion") await run.Documents.Received(1).CompleteReConsentBatchAsync(run.BatchId, CancellationToken.None);
        else await run.Documents.DidNotReceiveWithAnyArgs().CompleteReConsentBatchAsync(default);
    }

    private sealed class SyncRun
    {
        public ILegalDocumentSyncManagement Sync { get; } = Substitute.For<ILegalDocumentSyncManagement>();
        public ILegalDocumentRepository Documents { get; } = Substitute.For<ILegalDocumentRepository>();
        public ITeamService Teams { get; } = Substitute.For<ITeamService>();
        public IEmailService Email { get; } = Substitute.For<IEmailService>();
        public LegalDocument Document { get; } = MakeDocument("Required", true);
        public Guid BatchId => Document.CurrentVersion!.PendingReConsentBatchId!.Value;
        public LegalDocumentSyncRunner Runner { get; }
        public SyncRun(bool singleRecipient = false)
        {
            Sync.SyncAllDocumentsAsync(Arg.Any<CancellationToken>(), Arg.Any<ICollection<Exception>?>()).Returns(Array.Empty<LegalDocument>());
            Documents.GetDocumentsAsync(null, Arg.Any<CancellationToken>()).Returns(new[] { Document });
            var users = Substitute.For<IUserService>();
            var recipients = Enumerable.Range(0, singleRecipient ? 1 : 2).Select(_ =>
            {
                var user = new User { Id = Guid.NewGuid(), State = UserState.Active, BurnerName = "Member" };
                return UserInfo.Create(user, [new UserEmail { Id = Guid.NewGuid(), UserId = user.Id,
                    Email = $"{user.Id:N}@example.com", IsPrimary = true, IsVerified = true }], [], UserFixtures.Profile());
            }).ToList();
            Teams.GetTeamAsync(Document.TeamId, Arg.Any<CancellationToken>()).Returns(new TeamInfo(
                Document.TeamId, "Own", null, "own", true, false, SystemTeamType.None, false, false, false, false, default,
                recipients.Select(user => new TeamMemberInfo(Guid.NewGuid(), user.Id, "Member", user.Email, null, TeamMemberRole.Member, default)).ToList()));
            users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(recipients.ToDictionary(user => user.Id));
            var consents = Substitute.For<IConsentService>();
            consents.GetConsentMapForUsersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, IReadOnlySet<Guid>>());
            Runner = new(Sync, Email, TestConsentEmails.Create(), Teams, users, consents, Documents,
                NullLogger<LegalDocumentSyncRunner>.Instance);
        }
    }

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
