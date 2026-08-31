using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using CampaignServiceImpl = Humans.Campaigns.Services.CampaignService;

namespace Humans.Campaigns.Tests.Services;

/// <summary>
/// Owns its fixture rather than deriving from <c>Humans.Application.Tests</c>'
/// <c>ServiceTestHarness</c>: that harness is built around an in-memory
/// <c>UsersDbContext</c>, and inheriting it would grant a section test project
/// <c>InternalsVisibleTo</c> on <c>UsersDbContext</c> — the boundary the G5 split exists
/// to draw (nobodies-collective/Humans#866). An in-memory people/team registry backs
/// the cross-section stubs instead.
/// </summary>
public sealed class CampaignServiceTests
{
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 31, 12, 0));

    private readonly TestDbContextFactory<CampaignsDbContext> CampaignsDbFactory =
        new(new DbContextOptionsBuilder<CampaignsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private readonly CampaignsDbContext CampaignsDb;

    private readonly Dictionary<Guid, UserInfo> _people = [];
    private readonly Dictionary<Guid, TeamInfo> _teams = [];

    private readonly CampaignServiceImpl _service;
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly ITicketDiscountCodes _ticketDiscountCodes;

    public CampaignServiceTests()
    {
        CampaignsDb = CampaignsDbFactory.CreateDbContext();

        var repository = new CampaignRepository(CampaignsDbFactory);
        var teamService = Substitute.For<ITeamServiceRead>();
        var userEmailService = Substitute.For<IUserEmailService>();
        var userService = Substitute.For<IUserServiceRead>();
        _ticketDiscountCodes = Substitute.For<ITicketDiscountCodes>();

        // Stubs read the in-memory registries the Seed* helpers write to, so the existing
        // seed-then-exercise scenarios still drive the service end to end.
        teamService
            .GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _teams.GetValueOrDefault(call.ArgAt<Guid>(0)));

        teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, TeamInfo>)new Dictionary<Guid, TeamInfo>(_teams));

        userService
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(_people.GetValueOrDefault(call.ArgAt<Guid>(0))));

        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<Guid, UserInfo>)call
                .ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(_people.ContainsKey)
                .ToDictionary(id => id, id => _people[id]));

        userEmailService
            .GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<Guid, string>)call
                .ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(id => _people.TryGetValue(id, out var p) && !string.IsNullOrEmpty(p.Email))
                .ToDictionary(id => id, id => _people[id].Email!));

        _service = new CampaignServiceImpl(
            repository,
            teamService,
            userEmailService,
            userService,
            Substitute.For<INotificationEmitter>(),
            _emailService,
            _emailMessages,
            _ticketDiscountCodes,
            Clock,
            NullLogger<CampaignServiceImpl>.Instance);
    }

    [HumansFact]
    public async Task CreateAsync_CreatesCampaignInDraftStatus()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.CreateAsync(
            "Test Campaign", "A description",
            "Your code: {{Code}}", "<p>Hi {{Name}}, your code is {{Code}}</p>",
            null, userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.Campaign.Should().NotBeNull();
        result.Campaign!.Title.Should().Be("Test Campaign");
        result.Campaign.Description.Should().Be("A description");
        result.Campaign.Status.Should().Be(CampaignStatus.Draft);
        result.Campaign.CreatedAt.Should().Be(Clock.GetCurrentInstant());
        result.Campaign.CreatedByUserId.Should().Be(userId);

        var inDb = await CampaignsDb.Campaigns.FindAsync(result.Campaign.Id, Xunit.TestContext.Current.CancellationToken);
        inDb.Should().NotBeNull();
        inDb.Status.Should().Be(CampaignStatus.Draft);
    }

    [HumansFact]
    public async Task CreateAsync_BlankTitle_ReturnsValidationError()
    {
        var result = await _service.CreateAsync(
            "  ", "A description",
            "Subject", "Body",
            null, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("TitleRequired");
        (await CampaignsDb.Campaigns.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task ImportCodesAsync_CreatesCampaignCodeRows_SkipsDuplicates()
    {
        var campaign = await SeedCampaignAsync();

        await _service.ImportCodesAsync(campaign.Id, ["CODE1", "CODE2", "CODE1", "CODE3"], Xunit.TestContext.Current.CancellationToken);

        var codes = await CampaignsDb.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.Should().HaveCount(3);
        codes.Select(c => c.Code).Should().BeEquivalentTo("CODE1", "CODE2", "CODE3");
    }

    [HumansFact]
    public async Task ImportCodesAsync_SkipsExistingCodesInCampaign()
    {
        var campaign = await SeedCampaignAsync();

        // First import
        await _service.ImportCodesAsync(campaign.Id, ["CODE1", "CODE2"], Xunit.TestContext.Current.CancellationToken);
        // Second import with overlap
        await _service.ImportCodesAsync(campaign.Id, ["CODE2", "CODE3"], Xunit.TestContext.Current.CancellationToken);

        var codes = await CampaignsDb.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task GenerateAndImportDiscountCodesAsync_DraftCampaign_GeneratesAndImportsCodes()
    {
        var campaign = await SeedCampaignAsync();
        _ticketDiscountCodes
            .GenerateAsync(Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(["CODE-A", "CODE-B"]);

        var result = await _service.GenerateAndImportDiscountCodesAsync(
            campaign.Id, 2, "Fixed", 10m, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.GeneratedCount.Should().Be(2);
        await _ticketDiscountCodes.Received(1).GenerateAsync(
            Arg.Is<TicketDiscountCodeRequest>(r =>
                r.Count == 2 &&
                r.Kind == TicketDiscountKind.Fixed &&
                r.Value == 10m),
            Arg.Any<CancellationToken>());
        var codes = await CampaignsDb.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .Select(c => c.Code)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.Should().BeEquivalentTo("CODE-A", "CODE-B");
    }

    [HumansFact]
    public async Task GenerateAndImportDiscountCodesAsync_NonDraftCampaign_ReturnsNotDraft()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        var result = await _service.GenerateAndImportDiscountCodesAsync(
            campaign.Id, 2, "Fixed", 10m, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotDraft");
        await _ticketDiscountCodes.DidNotReceive().GenerateAsync(
            Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ActivateAsync_DraftWithCodes_TransitionsToActive()
    {
        var campaign = await SeedCampaignAsync();
        await _service.ImportCodesAsync(campaign.Id, ["CODE1"], Xunit.TestContext.Current.CancellationToken);

        await _service.ActivateAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        var updated = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        updated!.Status.Should().Be(CampaignStatus.Active);
    }

    [HumansFact(Timeout = 10000)]
    public async Task ActivateAsync_NoCodes_Throws()
    {
        var campaign = await SeedCampaignAsync();

        var act = () => _service.ActivateAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one code*");
    }

    [HumansFact]
    public async Task ActivateAsync_NotDraft_Throws()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        var act = () => _service.ActivateAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Draft*");
    }

    [HumansFact]
    public async Task UpdateAsync_ExistingCampaign_UpdatesFields()
    {
        var campaign = await SeedCampaignAsync();

        var updated = await _service.UpdateAsync(
            campaign.Id,
            "  Updated Campaign  ",
            "  Updated description  ",
            "  Updated subject  ",
            "  Updated body  ",
            "  reply@example.com  ", Xunit.TestContext.Current.CancellationToken);

        updated.Success.Should().BeTrue();

        var refreshed = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        refreshed.Should().NotBeNull();
        refreshed.Title.Should().Be("Updated Campaign");
        refreshed.Description.Should().Be("Updated description");
        refreshed.EmailSubject.Should().Be("Updated subject");
        refreshed.EmailBodyTemplate.Should().Be("Updated body");
        refreshed.ReplyToAddress.Should().Be("reply@example.com");
    }

    [HumansFact]
    public async Task UpdateAsync_BlankEmailSubject_ReturnsValidationError()
    {
        var campaign = await SeedCampaignAsync();

        var updated = await _service.UpdateAsync(
            campaign.Id,
            "Title",
            null,
            " ",
            "Body",
            null, Xunit.TestContext.Current.CancellationToken);

        updated.Success.Should().BeFalse();
        updated.ErrorKey.Should().Be("EmailSubjectRequired");
    }

    [HumansFact]
    public async Task GetDetailPageAsync_ReturnsComputedStats()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["A", "B", "C"]);
        var user = SeedUser(displayName: "Stats User");
        var grantedCode = await CampaignsDb.CampaignCodes
            .FirstAsync(c => c.CampaignId == campaign.Id && c.Code == "A", Xunit.TestContext.Current.CancellationToken);

        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = grantedCode.Id,
            UserId = user.Id,
            AssignedAt = Clock.GetCurrentInstant(),
            RedeemedAt = Clock.GetCurrentInstant(),
            LatestEmailStatus = EmailOutboxStatus.Failed
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var page = await _service.GetDetailPageAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page.Campaign.Id.Should().Be(campaign.Id);
        page.Stats.TotalCodes.Should().Be(3);
        page.Stats.AvailableCodes.Should().Be(2);
        page.Stats.FailedCount.Should().Be(1);
        page.Stats.SentCount.Should().Be(0);
        page.Stats.CodesRedeemed.Should().Be(1);
        page.Stats.TotalGrants.Should().Be(1);
    }

    [HumansFact]
    public async Task GetSendWavePageAsync_ReturnsTeamsAndSelectedPreview()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["A1", "A2"]);
        var user = SeedUser(displayName: "Wave User");
        SeedTeam("Beta Team");
        var alpha = SeedTeam("Alpha Team");
        SeedTeamMember(alpha.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var page = await _service.GetSendWavePageAsync(campaign.Id, alpha.Id, Xunit.TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page.Campaign.Id.Should().Be(campaign.Id);
        page.SelectedTeamId.Should().Be(alpha.Id);
        page.Preview.Should().NotBeNull();
        page.Preview!.EligibleCount.Should().Be(1);
        page.Teams.Select(t => t.Name).Should().ContainInOrder("Alpha Team", "Beta Team");
    }

    [HumansFact]
    public async Task GetCampaignIdForGrantAsync_ReturnsCampaignId()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["RESEND-CODE"]);
        var user = SeedUser(displayName: "Grant User");
        var team = SeedTeam("Grant Team");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        var grant = await CampaignsDb.CampaignGrants.SingleAsync(Xunit.TestContext.Current.CancellationToken);

        var campaignId = await _service.GetCampaignIdForGrantAsync(grant.Id, Xunit.TestContext.Current.CancellationToken);

        campaignId.Should().Be(campaign.Id);
    }

    [HumansFact]
    public async Task CompleteAsync_ActiveCampaign_TransitionsToCompleted()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        await _service.CompleteAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        var updated = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        updated!.Status.Should().Be(CampaignStatus.Completed);
    }

    [HumansFact]
    public async Task CompleteAsync_NotActive_Throws()
    {
        var campaign = await SeedCampaignAsync();

        var act = () => _service.CompleteAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Active*");
    }

    [HumansFact]
    public async Task SendWaveAsync_AssignsCodeToTeamMember_CreatesGrantAndEnqueuesEmail()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["CODE-A", "CODE-B"],
            emailSubject: "Hi {{Name}}, here is your code",
            emailBodyTemplate: "<p>Hi {{Name}}, your code is {{Code}}</p>");

        var user = SeedUser(displayName: "Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var count = await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);

        var grants = await CampaignsDb.CampaignGrants
            .Where(g => g.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants.Should().ContainSingle();
        grants[0].UserId.Should().Be(user.Id);
        grants[0].LatestEmailStatus.Should().Be(EmailOutboxStatus.Queued);

        // CampaignService delegates to IEmailService — verify the request was passed
        // through. Allocation orders by ImportOrder, so the first code goes out first.
        _emailMessages.Received(1).CampaignCode(
            Arg.Is<CampaignCodeEmailRequest>(r =>
                r.CampaignGrantId == grants[0].Id
                && r.UserId == user.Id
                && r.RecipientEmail == user.Email
                && r.Code == "CODE-A"));
    }

    [HumansFact]
    public async Task SendWaveAsync_SendsFactoryMessageBuiltFromRawTemplateValues()
    {
        // HTML-encoding and rendering happen inside the Email section (owner of the
        // outbox) — CampaignService must forward the raw subject/body/code/name to the
        // factory and hand the factory's message to SendAsync unmodified.
        var campaign = await SeedActiveCampaignWithCodesAsync(["A<B>C"],
            emailSubject: "Hi {{Name}}, your code",
            emailBodyTemplate: "<p>Hi {{Name}}, your code is {{Code}}</p>");

        var user = SeedUser(displayName: "O'Brien & Co");
        var team = SeedTeam("Gamma");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var factoryMessage = new EmailMessage(
            user.Email!, "O'Brien & Co", "subject", "body", "CampaignCode");
        _emailMessages.CampaignCode(Arg.Any<CampaignCodeEmailRequest>()).Returns(factoryMessage);

        await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        _emailMessages.Received(1).CampaignCode(
            Arg.Is<CampaignCodeEmailRequest>(r =>
                r.Subject == "Hi {{Name}}, your code"
                && r.MarkdownBody == "<p>Hi {{Name}}, your code is {{Code}}</p>"
                && r.Code == "A<B>C"
                && r.RecipientName == "O'Brien & Co"));
        await _emailService.Received(1).SendAsync(factoryMessage, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendWaveAsync_NotActive_Throws()
    {
        var campaign = await SeedCampaignAsync();
        await _service.ImportCodesAsync(campaign.Id, ["CODE-1"], Xunit.TestContext.Current.CancellationToken);
        var user = SeedUser(displayName: "Draft User");
        var team = SeedTeam("DraftTeam");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Active*");
        (await CampaignsDb.CampaignGrants.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task SendWaveAsync_EnqueueThrowsForOneGrant_FlipsOnlyThatGrantToFailed()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["ISO-1", "ISO-2"]);

        var user1 = SeedUser(displayName: "First");
        var user2 = SeedUser(displayName: "Second");
        var team = SeedTeam("Isolation");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var sendCalls = 0;
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++sendCalls == 1
                ? Task.FromException(new InvalidOperationException("enqueue boom"))
                : Task.CompletedTask);

        var count = await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        // The wave survives the throw: both grants exist, only the offending one is Failed.
        count.Should().Be(2);
        var grants = await CampaignsDb.CampaignGrants
            .Where(g => g.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants.Should().HaveCount(2);
        grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Failed).Should().Be(1);
        grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Queued).Should().Be(1);
    }

    [HumansFact]
    public async Task SendWaveAsync_DuplicatePrevention_ExcludesAlreadyGranted()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["CODE-1", "CODE-2", "CODE-3"]);

        var user1 = SeedUser(displayName: "Alice");
        var user2 = SeedUser(displayName: "Bob");
        var team = SeedTeam("Delta");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        // First wave sends to both
        var count1 = await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        count1.Should().Be(2);

        // Second wave should send to nobody (both already granted)
        var count2 = await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        count2.Should().Be(0);
    }

    [HumansFact]
    public async Task SendWaveAsync_InsufficientCodes_Throws()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["ONLY-ONE"]);

        var user1 = SeedUser(displayName: "Alice");
        var user2 = SeedUser(displayName: "Bob");
        var team = SeedTeam("Zeta");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Not enough codes*");
    }


    [HumansFact]
    public async Task ResendToGrantAsync_EnqueuesNewEmailAndResetsGrantStatus()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["RESEND-CODE"]);

        var user = SeedUser(displayName: "Dave");
        var team = SeedTeam("Theta");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        _emailMessages.ClearReceivedCalls();

        var grant = await CampaignsDb.CampaignGrants.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        grant.LatestEmailStatus = EmailOutboxStatus.Failed;
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.ResendToGrantAsync(grant.Id, Xunit.TestContext.Current.CancellationToken);

        _emailMessages.Received(1).CampaignCode(
            Arg.Is<CampaignCodeEmailRequest>(r => r.CampaignGrantId == grant.Id));

        ClearAllTrackers();
        var updatedGrant = await CampaignsDb.CampaignGrants.FindAsync(grant.Id, Xunit.TestContext.Current.CancellationToken);
        updatedGrant!.LatestEmailStatus.Should().Be(EmailOutboxStatus.Queued);
    }

    [HumansFact]
    public async Task RetryAllFailedAsync_EnqueuesEmailsForFailedGrantsOnly()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["FAIL-1", "FAIL-2"]);

        var user1 = SeedUser(displayName: "FailUser1");
        var user2 = SeedUser(displayName: "FailUser2");
        var team = SeedTeam("Iota");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        _emailMessages.ClearReceivedCalls();

        // Mark one as failed
        var grants = await CampaignsDb.CampaignGrants.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants[0].LatestEmailStatus = EmailOutboxStatus.Failed;
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.RetryAllFailedAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        // Only the failed grant should be re-enqueued.
        _emailMessages.Received(1).CampaignCode(
            Arg.Is<CampaignCodeEmailRequest>(r => r.CampaignGrantId == grants[0].Id));

        ClearAllTrackers();
        var retriedGrant = await CampaignsDb.CampaignGrants.FindAsync(grants[0].Id, Xunit.TestContext.Current.CancellationToken);
        retriedGrant!.LatestEmailStatus.Should().Be(EmailOutboxStatus.Queued);
    }

    [HumansFact]
    public async Task PreviewWaveSendAsync_ReturnsCorrectCounts()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["P1", "P2", "P3", "P4", "P5"]);

        var eligible = SeedUser(displayName: "Eligible");
        var alreadyGranted = SeedUser(displayName: "Granted");
        var otherUser = SeedUser(displayName: "Other");
        var team = SeedTeam("Preview");
        SeedTeamMember(team.Id, eligible.Id);
        SeedTeamMember(team.Id, alreadyGranted.Id);
        SeedTeamMember(team.Id, otherUser.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        // Grant a code to alreadyGranted user manually
        var code = await CampaignsDb.CampaignCodes.FirstAsync(c => c.CampaignId == campaign.Id, Xunit.TestContext.Current.CancellationToken);
        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = code.Id,
            UserId = alreadyGranted.Id,
            AssignedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var preview = await _service.PreviewWaveSendAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        preview.EligibleCount.Should().Be(2); // "Eligible" + "Other"
        preview.AlreadyGrantedExcluded.Should().Be(1);
        preview.CodesAvailable.Should().Be(4); // 5 total - 1 granted
        preview.CodesRemainingAfterSend.Should().Be(2); // 4 available - 2 eligible
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ExportsGrantRows()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["EXPORT-CODE"]);
        var user = SeedUser(displayName: "Export User");
        var code = await CampaignsDb.CampaignCodes.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = code.Id,
            UserId = user.Id,
            AssignedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var slices = await _service.ContributeForUserAsync(user.Id, Xunit.TestContext.Current.CancellationToken);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(Humans.Gdpr.Contracts.GdprExportSections.CampaignGrants);
        var rows = (System.Collections.IEnumerable)slices[0].Data!;
        rows.Cast<object>().Should().ContainSingle();
    }

    [HumansFact]
    public async Task EraseForUserAsync_DeletesOnlyThatUsersGrants()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["ERASE-1", "ERASE-2"]);
        var erased = SeedUser(displayName: "Erased");
        var kept = SeedUser(displayName: "Kept");
        var team = SeedTeam("Erasure");
        SeedTeamMember(team.Id, erased.Id);
        SeedTeamMember(team.Id, kept.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        await _service.SendWaveAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        await _service.EraseForUserAsync(erased.Id, Xunit.TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var remaining = await CampaignsDb.CampaignGrants.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle();
        remaining[0].UserId.Should().Be(kept.Id);
    }

    [HumansFact]
    public async Task ReassignAsync_MovesGrantsToTarget_TargetWinsOnCollision()
    {
        // Campaign A: both source and target hold a grant -> target's survives.
        // Campaign B: only source holds a grant -> it moves to target.
        var campaignA = await SeedActiveCampaignWithCodesAsync(["A-SRC", "A-TGT"]);
        var campaignB = await SeedActiveCampaignWithCodesAsync(["B-SRC"]);
        var source = SeedUser(displayName: "Source");
        var target = SeedUser(displayName: "Target");
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var codes = await CampaignsDb.CampaignCodes.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        Guid GrantOn(string code, Guid userId)
        {
            var c = codes.Single(x => string.Equals(x.Code, code, StringComparison.Ordinal));
            var id = Guid.NewGuid();
            CampaignsDb.CampaignGrants.Add(new CampaignGrant
            {
                Id = id,
                CampaignId = c.CampaignId,
                CampaignCodeId = c.Id,
                UserId = userId,
                AssignedAt = Clock.GetCurrentInstant()
            });
            return id;
        }
        GrantOn("A-SRC", source.Id);
        var targetGrantA = GrantOn("A-TGT", target.Id);
        var sourceGrantB = GrantOn("B-SRC", source.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        ClearAllTrackers();

        await _service.ReassignAsync(
            source.Id, target.Id, Guid.NewGuid(), Clock.GetCurrentInstant(),
            Xunit.TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var grants = await CampaignsDb.CampaignGrants.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants.Should().OnlyContain(g => g.UserId == target.Id);
        grants.Select(g => g.Id).Should().BeEquivalentTo([targetGrantA, sourceGrantB]);
        grants.Single(g => g.CampaignId == campaignA.Id).Id.Should().Be(targetGrantA);
    }

    private async Task<Campaign> SeedCampaignAsync(
        CampaignStatus status = CampaignStatus.Draft,
        string emailSubject = "Your code: {{Code}}",
        string emailBodyTemplate = "<p>Hi {{Name}}, your code is {{Code}}</p>")
    {
        var creatorId = Guid.NewGuid();
        SeedUser(creatorId, "Creator");

        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Test Campaign",
            EmailSubject = emailSubject,
            EmailBodyTemplate = emailBodyTemplate,
            Status = status,
            CreatedAt = Clock.GetCurrentInstant(),
            CreatedByUserId = creatorId
        };
        CampaignsDb.Campaigns.Add(campaign);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        ClearAllTrackers();
        return campaign;
    }

    private async Task<Campaign> SeedActiveCampaignWithCodesAsync(
        string[] codes,
        string emailSubject = "Your code: {{Code}}",
        string emailBodyTemplate = "<p>Hi {{Name}}, your code is {{Code}}</p>")
    {
        var campaign = await SeedCampaignAsync(
            CampaignStatus.Active,
            emailSubject,
            emailBodyTemplate);

        var now = Clock.GetCurrentInstant();
        for (var i = 0; i < codes.Length; i++)
        {
            CampaignsDb.CampaignCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                Code = codes[i],
                ImportOrder = i + 1,
                ImportedAt = now
            });
        }
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        ClearAllTrackers();
        return campaign;
    }

    // A section test project cannot see the Users section's tables, so the registry
    // holds the projections the service actually consumes: UserInfo and TeamInfo.

    private UserInfo SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var userId = id ?? Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = Clock.GetCurrentInstant()
        };
        var info = UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            communicationPreferences: []);
        _people[userId] = info;
        return info;
    }

    private TeamInfo SeedTeam(string name)
    {
        var team = new TeamInfo(
            Guid.NewGuid(), name, null, name.ToLowerInvariant().Replace(" ", "-"),
            IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
            IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false,
            Clock.GetCurrentInstant(), []);
        _teams[team.Id] = team;
        return team;
    }

    private void SeedTeamMember(Guid teamId, Guid userId)
    {
        _people.TryGetValue(userId, out var person);
        _teams[teamId].Members.Add(new TeamMemberInfo(
            Guid.NewGuid(), userId, person?.BurnerName ?? string.Empty,
            person?.Email, null, TeamMemberRole.Member, Clock.GetCurrentInstant()));
    }

    private Task SaveAllAsync(CancellationToken ct = default) => CampaignsDb.SaveChangesAsync(ct);

    private void ClearAllTrackers() => CampaignsDb.ChangeTracker.Clear();
}
