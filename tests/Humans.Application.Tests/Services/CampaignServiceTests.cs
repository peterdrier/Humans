using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampaignServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampaignService _service;
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    public CampaignServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.EnvironmentName.Returns("Production");

        var emailSettings = Options.Create(new EmailSettings
        {
            BaseUrl = "https://humans.nobodies.team"
        });

        _service = new CampaignService(
            _dbContext,
            _clock,
            _metrics,
            Substitute.For<INotificationService>(),
            Substitute.For<ICommunicationPreferenceService>(),
            emailSettings,
            hostEnvironment,
            NullLogger<CampaignService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // CreateAsync
    // ==========================================================================

    [Fact]
    public async Task CreateAsync_CreatesCampaignInDraftStatus()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.CreateAsync(
            "Test Campaign", "A description",
            "Your code: {{Code}}", "<p>Hi {{Name}}, your code is {{Code}}</p>",
            null, userId);

        result.Title.Should().Be("Test Campaign");
        result.Description.Should().Be("A description");
        result.Status.Should().Be(CampaignStatus.Draft);
        result.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        result.CreatedByUserId.Should().Be(userId);

        var inDb = await _dbContext.Campaigns.FindAsync(result.Id);
        inDb.Should().NotBeNull();
        inDb!.Status.Should().Be(CampaignStatus.Draft);
    }

    // ==========================================================================
    // ImportCodesAsync
    // ==========================================================================

    [Fact]
    public async Task ImportCodesAsync_CreatesCampaignCodeRows_SkipsDuplicates()
    {
        var campaign = await SeedCampaignAsync();

        await _service.ImportCodesAsync(campaign.Id, new[] { "CODE1", "CODE2", "CODE1", "CODE3" });

        var codes = await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .ToListAsync();
        codes.Should().HaveCount(3);
        codes.Select(c => c.Code).Should().BeEquivalentTo(new[] { "CODE1", "CODE2", "CODE3" });
    }

    [Fact]
    public async Task ImportCodesAsync_SkipsExistingCodesInCampaign()
    {
        var campaign = await SeedCampaignAsync();

        // First import
        await _service.ImportCodesAsync(campaign.Id, new[] { "CODE1", "CODE2" });
        // Second import with overlap
        await _service.ImportCodesAsync(campaign.Id, new[] { "CODE2", "CODE3" });

        var codes = await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .ToListAsync();
        codes.Should().HaveCount(3);
    }

    // ==========================================================================
    // ActivateAsync
    // ==========================================================================

    [Fact]
    public async Task ActivateAsync_DraftWithCodes_TransitionsToActive()
    {
        var campaign = await SeedCampaignAsync();
        await _service.ImportCodesAsync(campaign.Id, new[] { "CODE1" });

        await _service.ActivateAsync(campaign.Id);

        var updated = await _dbContext.Campaigns.FindAsync(campaign.Id);
        updated!.Status.Should().Be(CampaignStatus.Active);
    }

    [Fact]
    public async Task ActivateAsync_NoCodes_Throws()
    {
        var campaign = await SeedCampaignAsync();

        var act = () => _service.ActivateAsync(campaign.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one code*");
    }

    [Fact]
    public async Task ActivateAsync_NotDraft_Throws()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        var act = () => _service.ActivateAsync(campaign.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Draft*");
    }

    [Fact]
    public async Task UpdateAsync_ExistingCampaign_UpdatesFields()
    {
        var campaign = await SeedCampaignAsync();

        var updated = await _service.UpdateAsync(
            campaign.Id,
            "  Updated Campaign  ",
            "  Updated description  ",
            "  Updated subject  ",
            "  Updated body  ",
            "  reply@example.com  ");

        updated.Should().BeTrue();

        var refreshed = await _dbContext.Campaigns.FindAsync(campaign.Id);
        refreshed.Should().NotBeNull();
        refreshed!.Title.Should().Be("Updated Campaign");
        refreshed.Description.Should().Be("Updated description");
        refreshed.EmailSubject.Should().Be("Updated subject");
        refreshed.EmailBodyTemplate.Should().Be("Updated body");
        refreshed.ReplyToAddress.Should().Be("reply@example.com");
    }

    [Fact]
    public async Task GetDetailPageAsync_ReturnsComputedStats()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "A", "B", "C" });
        var user = SeedUser(displayName: "Stats User");
        var grantedCode = await _dbContext.CampaignCodes
            .FirstAsync(c => c.CampaignId == campaign.Id && c.Code == "A");

        _dbContext.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = grantedCode.Id,
            UserId = user.Id,
            AssignedAt = _clock.GetCurrentInstant(),
            RedeemedAt = _clock.GetCurrentInstant(),
            LatestEmailStatus = EmailOutboxStatus.Failed
        });
        await _dbContext.SaveChangesAsync();

        var page = await _service.GetDetailPageAsync(campaign.Id);

        page.Should().NotBeNull();
        page!.Campaign.Id.Should().Be(campaign.Id);
        page.Stats.TotalCodes.Should().Be(3);
        page.Stats.AvailableCodes.Should().Be(2);
        page.Stats.FailedCount.Should().Be(1);
        page.Stats.SentCount.Should().Be(0);
        page.Stats.CodesRedeemed.Should().Be(1);
        page.Stats.TotalGrants.Should().Be(1);
    }

    [Fact]
    public async Task GetSendWavePageAsync_ReturnsTeamsAndSelectedPreview()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "A1", "A2" });
        var user = SeedUser(displayName: "Wave User");
        var beta = SeedTeam("Beta Team");
        var alpha = SeedTeam("Alpha Team");
        SeedTeamMember(alpha.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var page = await _service.GetSendWavePageAsync(campaign.Id, alpha.Id);

        page.Should().NotBeNull();
        page!.Campaign.Id.Should().Be(campaign.Id);
        page.SelectedTeamId.Should().Be(alpha.Id);
        page.Preview.Should().NotBeNull();
        page.Preview!.EligibleCount.Should().Be(1);
        page.Teams.Select(t => t.Name).Should().ContainInOrder("Alpha Team", "Beta Team");
    }

    [Fact]
    public async Task GetCampaignIdForGrantAsync_ReturnsCampaignId()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "RESEND-CODE" });
        var user = SeedUser(displayName: "Grant User");
        var team = SeedTeam("Grant Team");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();
        await _service.SendWaveAsync(campaign.Id, team.Id);
        var grant = await _dbContext.CampaignGrants.SingleAsync();

        var campaignId = await _service.GetCampaignIdForGrantAsync(grant.Id);

        campaignId.Should().Be(campaign.Id);
    }

    // ==========================================================================
    // CompleteAsync
    // ==========================================================================

    [Fact]
    public async Task CompleteAsync_ActiveCampaign_TransitionsToCompleted()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        await _service.CompleteAsync(campaign.Id);

        var updated = await _dbContext.Campaigns.FindAsync(campaign.Id);
        updated!.Status.Should().Be(CampaignStatus.Completed);
    }

    [Fact]
    public async Task CompleteAsync_NotActive_Throws()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Draft);

        var act = () => _service.CompleteAsync(campaign.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Active*");
    }

    // ==========================================================================
    // SendWaveAsync
    // ==========================================================================

    [Fact]
    public async Task SendWaveAsync_AssignsCodeToTeamMember_CreatesGrantAndOutbox()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(
            new[] { "CODE-A", "CODE-B" },
            emailSubject: "Hi {{Name}}, here is your code",
            emailBodyTemplate: "<p>Hi {{Name}}, your code is {{Code}}</p>");

        var user = SeedUser(displayName: "Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var count = await _service.SendWaveAsync(campaign.Id, team.Id);

        count.Should().Be(1);

        var grants = await _dbContext.CampaignGrants
            .Where(g => g.CampaignId == campaign.Id)
            .ToListAsync();
        grants.Should().ContainSingle();
        grants[0].UserId.Should().Be(user.Id);
        grants[0].LatestEmailStatus.Should().Be(EmailOutboxStatus.Queued);

        var outbox = await _dbContext.EmailOutboxMessages
            .Where(m => m.CampaignGrantId == grants[0].Id)
            .ToListAsync();
        outbox.Should().ContainSingle();
        outbox[0].TemplateName.Should().Be("campaign_code");
        outbox[0].RecipientEmail.Should().Be(user.Email);
        outbox[0].Status.Should().Be(EmailOutboxStatus.Queued);
    }

    [Fact]
    public async Task SendWaveAsync_SubstitutesNameInSubject()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(
            new[] { "CODE-1" },
            emailSubject: "Hi {{Name}}, your code");

        var user = SeedUser(displayName: "Charlie");
        var team = SeedTeam("Gamma");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        await _service.SendWaveAsync(campaign.Id, team.Id);

        var outbox = await _dbContext.EmailOutboxMessages.SingleAsync();
        outbox.Subject.Should().Be("Hi Charlie, your code");
    }

    [Fact]
    public async Task SendWaveAsync_DuplicatePrevention_ExcludesAlreadyGranted()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "CODE-1", "CODE-2", "CODE-3" });

        var user1 = SeedUser(displayName: "Alice");
        var user2 = SeedUser(displayName: "Bob");
        var team = SeedTeam("Delta");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await _dbContext.SaveChangesAsync();

        // First wave sends to both
        var count1 = await _service.SendWaveAsync(campaign.Id, team.Id);
        count1.Should().Be(2);

        // Second wave should send to nobody (both already granted)
        var count2 = await _service.SendWaveAsync(campaign.Id, team.Id);
        count2.Should().Be(0);
    }

    [Fact]
    public async Task SendWaveAsync_InsufficientCodes_Throws()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "ONLY-ONE" });

        var user1 = SeedUser(displayName: "Alice");
        var user2 = SeedUser(displayName: "Bob");
        var team = SeedTeam("Zeta");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.SendWaveAsync(campaign.Id, team.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Not enough codes*");
    }

    [Fact]
    public async Task SendWaveAsync_HtmlEncodesValuesInBody()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(
            new[] { "A<B>C" },
            emailBodyTemplate: "<p>Code: {{Code}}, Name: {{Name}}</p>");

        var user = SeedUser(displayName: "O'Brien & Co");
        var team = SeedTeam("Eta");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        await _service.SendWaveAsync(campaign.Id, team.Id);

        var outbox = await _dbContext.EmailOutboxMessages.SingleAsync();
        // Body should contain HTML-encoded values
        outbox.HtmlBody.Should().Contain("A&lt;B&gt;C");
        outbox.HtmlBody.Should().Contain("O&#39;Brien &amp; Co");
    }

    // ==========================================================================
    // ResendToGrantAsync
    // ==========================================================================

    [Fact]
    public async Task ResendToGrantAsync_CreatesNewOutboxMessage()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "RESEND-CODE" });

        var user = SeedUser(displayName: "Dave");
        var team = SeedTeam("Theta");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        await _service.SendWaveAsync(campaign.Id, team.Id);

        var grant = await _dbContext.CampaignGrants.SingleAsync();
        grant.LatestEmailStatus = EmailOutboxStatus.Failed;
        await _dbContext.SaveChangesAsync();

        await _service.ResendToGrantAsync(grant.Id);

        var outboxMessages = await _dbContext.EmailOutboxMessages
            .Where(m => m.CampaignGrantId == grant.Id)
            .ToListAsync();
        outboxMessages.Should().HaveCount(2);

        var updatedGrant = await _dbContext.CampaignGrants.FindAsync(grant.Id);
        updatedGrant!.LatestEmailStatus.Should().Be(EmailOutboxStatus.Queued);
    }

    // ==========================================================================
    // RetryAllFailedAsync
    // ==========================================================================

    [Fact]
    public async Task RetryAllFailedAsync_CreatesOutboxForFailedGrants()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "FAIL-1", "FAIL-2" });

        var user1 = SeedUser(displayName: "FailUser1");
        var user2 = SeedUser(displayName: "FailUser2");
        var team = SeedTeam("Iota");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await _dbContext.SaveChangesAsync();

        await _service.SendWaveAsync(campaign.Id, team.Id);

        // Mark one as failed
        var grants = await _dbContext.CampaignGrants.ToListAsync();
        grants[0].LatestEmailStatus = EmailOutboxStatus.Failed;
        await _dbContext.SaveChangesAsync();

        await _service.RetryAllFailedAsync(campaign.Id);

        // Should have 3 outbox messages total (2 original + 1 retry)
        var outboxCount = await _dbContext.EmailOutboxMessages.CountAsync();
        outboxCount.Should().Be(3);

        var retriedGrant = await _dbContext.CampaignGrants.FindAsync(grants[0].Id);
        retriedGrant!.LatestEmailStatus.Should().Be(EmailOutboxStatus.Queued);
    }

    // ==========================================================================
    // PreviewWaveSendAsync
    // ==========================================================================

    [Fact]
    public async Task PreviewWaveSendAsync_ReturnsCorrectCounts()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(new[] { "P1", "P2", "P3", "P4", "P5" });

        var eligible = SeedUser(displayName: "Eligible");
        var alreadyGranted = SeedUser(displayName: "Granted");
        var otherUser = SeedUser(displayName: "Other");
        var team = SeedTeam("Preview");
        SeedTeamMember(team.Id, eligible.Id);
        SeedTeamMember(team.Id, alreadyGranted.Id);
        SeedTeamMember(team.Id, otherUser.Id);
        await _dbContext.SaveChangesAsync();

        // Grant a code to alreadyGranted user manually
        var code = await _dbContext.CampaignCodes.FirstAsync(c => c.CampaignId == campaign.Id);
        _dbContext.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = code.Id,
            UserId = alreadyGranted.Id,
            AssignedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var preview = await _service.PreviewWaveSendAsync(campaign.Id, team.Id);

        preview.EligibleCount.Should().Be(2); // "Eligible" + "Other"
        preview.AlreadyGrantedExcluded.Should().Be(1);
        preview.UnsubscribedExcluded.Should().Be(0);
        preview.CodesAvailable.Should().Be(4); // 5 total - 1 granted
        preview.CodesRemainingAfterSend.Should().Be(2); // 4 available - 2 eligible
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private User SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var userId = id ?? Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private Team SeedTeam(string name)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private TeamMember SeedTeamMember(Guid teamId, Guid userId)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamMembers.Add(member);
        return member;
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
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = creatorId
        };
        _dbContext.Campaigns.Add(campaign);
        await _dbContext.SaveChangesAsync();
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

        var now = _clock.GetCurrentInstant();
        foreach (var code in codes)
        {
            _dbContext.CampaignCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                Code = code,
                ImportedAt = now
            });
        }
        await _dbContext.SaveChangesAsync();
        return campaign;
    }
}
