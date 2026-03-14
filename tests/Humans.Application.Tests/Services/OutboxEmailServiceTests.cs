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
            FromAddress = "noreply@nobodies.team",
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
        _renderer.RenderEmailVerification("Bob", "bob@example.com", "https://verify", "en")
            .Returns(new EmailContent("Verify Email", "<p>Click to verify</p>"));

        await _service.SendEmailVerificationAsync("bob@example.com", "Bob", "https://verify", "en");

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
}
