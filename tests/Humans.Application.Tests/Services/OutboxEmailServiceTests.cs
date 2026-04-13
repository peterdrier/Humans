using AwesomeAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class OutboxEmailServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly OutboxEmailService _service;
    private readonly IEmailRenderer _renderer = Substitute.For<IEmailRenderer>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly IBackgroundJobClient _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();

    public OutboxEmailServiceTests()
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
            BaseUrl = "https://humans.nobodies.team",
            FromAddress = "humans@nobodies.team",
            FromName = "Nobodies Collective"
        });

        _service = new OutboxEmailService(
            _dbContext,
            _renderer,
            _metrics,
            _clock,
            hostEnvironment,
            emailSettings,
            _backgroundJobClient,
            _commPrefService,
            NullLogger<OutboxEmailService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_CreatesOutboxRowWithCorrectFields()
    {
        _renderer.RenderWelcome("Alice", "en")
            .Returns(new EmailContent("Welcome!", "<p>Hello Alice</p>"));

        await _service.SendWelcomeEmailAsync("alice@example.com", "Alice", "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);

        var msg = messages[0];
        msg.RecipientEmail.Should().Be("alice@example.com");
        msg.RecipientName.Should().Be("Alice");
        msg.Subject.Should().Be("Welcome!");
        msg.HtmlBody.Should().Contain("<p>Hello Alice</p>");
        msg.PlainTextBody.Should().NotBeNullOrEmpty();
        msg.TemplateName.Should().Be("welcome");
        msg.Status.Should().Be(EmailOutboxStatus.Queued);
        msg.CreatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_RecordsEmailQueuedMetric()
    {
        _renderer.RenderWelcome("Alice", "en")
            .Returns(new EmailContent("Welcome!", "<p>Hello</p>"));

        await _service.SendWelcomeEmailAsync("alice@example.com", "Alice", "en");

        _metrics.Received(1).RecordEmailQueued("welcome");
    }

    [Fact]
    public async Task SendEmailVerificationAsync_CreatesOutboxRowAndEnqueuesHangfireJob()
    {
        _renderer.RenderEmailVerification("Bob", "bob@example.com", "https://verify", false, "en")
            .Returns(new EmailContent("Verify Email", "<p>Click to verify</p>"));

        await _service.SendEmailVerificationAsync("bob@example.com", "Bob", "https://verify", culture: "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);

        var msg = messages[0];
        msg.TemplateName.Should().Be("email_verification");
        msg.Status.Should().Be(EmailOutboxStatus.Queued);

        // Verify Hangfire job was enqueued
        _backgroundJobClient.Received(1).Create(
            Arg.Any<Hangfire.Common.Job>(),
            Arg.Any<Hangfire.States.IState>());
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_SetsReplyToOnOutboxMessage()
    {
        _renderer.RenderFacilitatedMessage("Charlie", "Dave", "Hey!", true, "dave@example.com", "en")
            .Returns(new EmailContent("Message from Dave", "<p>Hey!</p>"));

        await _service.SendFacilitatedMessageAsync(
            "charlie@example.com", "Charlie", "Dave", "Hey!",
            includeContactInfo: true, senderEmail: "dave@example.com", culture: "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);
        messages[0].ReplyTo.Should().Be("dave@example.com");
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_NoContactInfo_ReplyToIsNull()
    {
        _renderer.RenderFacilitatedMessage("Charlie", "Dave", "Hey!", false, null, "en")
            .Returns(new EmailContent("Message from Dave", "<p>Hey!</p>"));

        await _service.SendFacilitatedMessageAsync(
            "charlie@example.com", "Charlie", "Dave", "Hey!",
            includeContactInfo: false, senderEmail: null, culture: "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);
        messages[0].ReplyTo.Should().BeNull();
    }

    [Fact]
    public async Task SendApplicationApprovedAsync_CreatesOutboxRowWithCorrectTemplateName()
    {
        _renderer.RenderApplicationApproved("Eve", MembershipTier.Colaborador, "en")
            .Returns(new EmailContent("Approved!", "<p>Congrats</p>"));

        await _service.SendApplicationApprovedAsync(
            "eve@example.com", "Eve", MembershipTier.Colaborador, "en");

        var msg = await _dbContext.EmailOutboxMessages.SingleAsync();
        msg.TemplateName.Should().Be("application_approved");
        msg.RecipientEmail.Should().Be("eve@example.com");
        _metrics.Received(1).RecordEmailQueued("application_approved");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_DoesNotEnqueueHangfireJob()
    {
        _renderer.RenderWelcome("Alice", "en")
            .Returns(new EmailContent("Welcome!", "<p>Hello</p>"));

        await _service.SendWelcomeEmailAsync("alice@example.com", "Alice", "en");

        // Welcome email should NOT trigger immediate processing
        _backgroundJobClient.DidNotReceive().Create(
            Arg.Any<Hangfire.Common.Job>(),
            Arg.Any<Hangfire.States.IState>());
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserOptedOutOfCategory_DoesNotCreateOutboxRow()
    {
        var userId = Guid.NewGuid();
        _dbContext.UserEmails.Add(new Humans.Domain.Entities.UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "charlie@example.com",
            IsVerified = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        _commPrefService.IsOptedOutAsync(userId, MessageCategory.TeamUpdates, Arg.Any<CancellationToken>())
            .Returns(true);
        _commPrefService.GenerateUnsubscribeHeaders(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        _commPrefService.GenerateBrowserUnsubscribeUrl(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns("https://example.com/unsubscribe/token");

        _renderer.RenderAddedToTeam(
                "Charlie", "Alpha Team", "alpha", Arg.Any<System.Collections.Generic.List<(string Name, string? Url)>>(), null)
            .Returns(new EmailContent("Added to Alpha Team", "<p>You joined Alpha Team</p>"));

        await _service.SendAddedToTeamAsync(
            "charlie@example.com", "Charlie", "Alpha Team", "alpha",
            Array.Empty<(string Name, string? Url)>());

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().BeEmpty("the email should have been suppressed because the user opted out of TeamUpdates");
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserOptedInToCategory_CreatesOutboxRow()
    {
        var userId = Guid.NewGuid();
        _dbContext.UserEmails.Add(new Humans.Domain.Entities.UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "dana@example.com",
            IsVerified = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        _commPrefService.IsOptedOutAsync(userId, MessageCategory.TeamUpdates, Arg.Any<CancellationToken>())
            .Returns(false);
        _commPrefService.GenerateUnsubscribeHeaders(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        _commPrefService.GenerateBrowserUnsubscribeUrl(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns("https://example.com/unsubscribe/token");

        _renderer.RenderAddedToTeam(
                "Dana", "Beta Team", "beta", Arg.Any<System.Collections.Generic.List<(string Name, string? Url)>>(), null)
            .Returns(new EmailContent("Added to Beta Team", "<p>You joined Beta Team</p>"));

        await _service.SendAddedToTeamAsync(
            "dana@example.com", "Dana", "Beta Team", "beta",
            Array.Empty<(string Name, string? Url)>());

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1, "opted-in user should receive the email");
        messages[0].RecipientEmail.Should().Be("dana@example.com");
        messages[0].TemplateName.Should().Be("added_to_team");
    }
}
