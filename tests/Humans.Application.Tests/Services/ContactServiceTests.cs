using AwesomeAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ContactServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly ICommunicationPreferenceService _communicationPreferenceService;
    private readonly UserManager<User> _userManager;
    private readonly ContactService _service;

    public ContactServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _auditLogService = Substitute.For<IAuditLogService>();
        _communicationPreferenceService = Substitute.For<ICommunicationPreferenceService>();

        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        // Default: CreateAsync succeeds and adds user to DbContext
        _userManager.CreateAsync(Arg.Any<User>())
            .Returns(callInfo =>
            {
                var user = callInfo.Arg<User>();
                if (user.Id == Guid.Empty) user.Id = Guid.NewGuid();
                user.NormalizedEmail = user.Email?.ToUpperInvariant();
                user.NormalizedUserName = user.UserName?.ToUpperInvariant();
                _dbContext.Users.Add(user);
                _dbContext.SaveChanges();
                return IdentityResult.Success;
            });

        _service = new ContactService(
            _dbContext,
            _userManager,
            _auditLogService,
            _communicationPreferenceService,
            _clock,
            NullLogger<ContactService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // CreateContactAsync
    // ==========================================================================

    [Fact]
    public async Task CreateContactAsync_CreatesUserWithContactAccountType()
    {
        var result = await _service.CreateContactAsync(
            "ticket@example.com", "Ticket Buyer", ContactSource.TicketTailor, "TT-123");

        result.AccountType.Should().Be(AccountType.Contact);
        result.ContactSource.Should().Be(ContactSource.TicketTailor);
        result.ExternalSourceId.Should().Be("TT-123");
        result.Email.Should().Be("ticket@example.com");
        result.DisplayName.Should().Be("Ticket Buyer");
        result.IsContact.Should().BeTrue();
    }

    [Fact]
    public async Task CreateContactAsync_CreatesUserEmailRecord()
    {
        var result = await _service.CreateContactAsync(
            "sub@example.com", "Subscriber", ContactSource.MailerLite);

        var userEmail = await _dbContext.UserEmails
            .FirstOrDefaultAsync(ue => ue.UserId == result.Id);

        userEmail.Should().NotBeNull();
        userEmail!.Email.Should().Be("sub@example.com");
        userEmail.IsVerified.Should().BeTrue();
        userEmail.IsOAuth.Should().BeFalse();
    }

    [Fact]
    public async Task CreateContactAsync_SetsCommunicationPreferences()
    {
        await _service.CreateContactAsync(
            "marketing@example.com", "Test", ContactSource.Manual);

        // Should have called UpdatePreferenceAsync for Marketing (opted in)
        await _communicationPreferenceService.Received(1).UpdatePreferenceAsync(
            Arg.Any<Guid>(), MessageCategory.Marketing, false, "ContactCreation", Arg.Any<CancellationToken>());

        // Should have called UpdatePreferenceAsync for EventOperations (opted out)
        await _communicationPreferenceService.Received(1).UpdatePreferenceAsync(
            Arg.Any<Guid>(), MessageCategory.EventOperations, true, "ContactCreation", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateContactAsync_ExistingContactEmail_ReturnsExisting()
    {
        var first = await _service.CreateContactAsync(
            "dup@example.com", "First", ContactSource.MailerLite);

        var second = await _service.CreateContactAsync(
            "dup@example.com", "Second", ContactSource.TicketTailor);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task CreateContactAsync_ExistingMemberEmail_Throws()
    {
        // Seed a member user directly
        var member = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = "Member",
            UserName = "member@example.com",
            Email = "member@example.com",
            NormalizedEmail = "MEMBER@EXAMPLE.COM",
            AccountType = AccountType.Member
        };
        _dbContext.Users.Add(member);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.CreateContactAsync(
            "member@example.com", "Duplicate", ContactSource.Manual);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*member account already exists*");
    }

    // ==========================================================================
    // FindContactByEmailAsync
    // ==========================================================================

    [Fact]
    public async Task FindContactByEmailAsync_ReturnsOnlyContacts()
    {
        // Seed a contact
        var contact = SeedContact("find@example.com");
        // Seed a member with different email
        SeedMember("member@example.com");
        await _dbContext.SaveChangesAsync();

        var result = await _service.FindContactByEmailAsync("find@example.com");
        result.Should().NotBeNull();
        result!.Id.Should().Be(contact.Id);

        var memberResult = await _service.FindContactByEmailAsync("member@example.com");
        memberResult.Should().BeNull();
    }

    // ==========================================================================
    // FindContactByExternalIdAsync
    // ==========================================================================

    [Fact]
    public async Task FindContactByExternalIdAsync_MatchesSourceAndId()
    {
        var contact = SeedContact("ext@example.com", ContactSource.TicketTailor, "TT-456");
        await _dbContext.SaveChangesAsync();

        var result = await _service.FindContactByExternalIdAsync(
            ContactSource.TicketTailor, "TT-456");
        result.Should().NotBeNull();
        result!.Id.Should().Be(contact.Id);

        // Wrong source
        var wrong = await _service.FindContactByExternalIdAsync(
            ContactSource.MailerLite, "TT-456");
        wrong.Should().BeNull();
    }

    // ==========================================================================
    // GetFilteredContactsAsync
    // ==========================================================================

    [Fact]
    public async Task GetFilteredContactsAsync_ReturnsOnlyContacts()
    {
        SeedContact("contact1@example.com");
        SeedContact("contact2@example.com");
        SeedMember("member@example.com");
        await _dbContext.SaveChangesAsync();

        var results = await _service.GetFilteredContactsAsync(null);
        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.Email.Should().Contain("contact"));
    }

    // ==========================================================================
    // MergeContactToMemberAsync
    // ==========================================================================

    [Fact]
    public async Task MergeContactToMemberAsync_DeactivatesContact()
    {
        var contact = SeedContact("merge@example.com");
        var member = SeedMember("member@test.com");
        await _dbContext.SaveChangesAsync();

        await _service.MergeContactToMemberAsync(contact, member, null, "test-merge");

        var updated = await _dbContext.Users.FindAsync(contact.Id);
        updated!.AccountType.Should().Be(AccountType.Deactivated);
    }

    [Fact]
    public async Task MergeContactToMemberAsync_MigratesPreferences_MemberWins()
    {
        var contact = SeedContact("pref@example.com");
        var member = SeedMember("member@test.com");

        // Contact has Marketing opted-in
        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = contact.Id,
            Category = MessageCategory.Marketing,
            OptedOut = false,
            UpdatedAt = _clock.GetCurrentInstant(),
            UpdateSource = "ContactCreation",
            User = contact
        });

        // Member already has Marketing opted-out (member's choice wins)
        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = member.Id,
            Category = MessageCategory.Marketing,
            OptedOut = true,
            UpdatedAt = _clock.GetCurrentInstant(),
            UpdateSource = "Profile",
            User = member
        });

        // Contact has CommunityUpdates (member doesn't) — should migrate
        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = contact.Id,
            Category = MessageCategory.CommunityUpdates,
            OptedOut = false,
            UpdatedAt = _clock.GetCurrentInstant(),
            UpdateSource = "ContactCreation",
            User = contact
        });

        await _dbContext.SaveChangesAsync();

        await _service.MergeContactToMemberAsync(contact, member, null, "test-merge");

        // Member's Marketing should still be opted-out (their preference wins)
        var memberMarketing = await _dbContext.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == member.Id && cp.Category == MessageCategory.Marketing);
        memberMarketing.Should().NotBeNull();
        memberMarketing!.OptedOut.Should().BeTrue();

        // Member should now have CommunityUpdates (migrated from contact)
        var memberCommunity = await _dbContext.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == member.Id && cp.Category == MessageCategory.CommunityUpdates);
        memberCommunity.Should().NotBeNull();
        memberCommunity!.OptedOut.Should().BeFalse();

        // Contact should have no preferences left
        var contactPrefs = await _dbContext.CommunicationPreferences
            .Where(cp => cp.UserId == contact.Id).ToListAsync();
        contactPrefs.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeContactToMemberAsync_AuditLogged()
    {
        var contact = SeedContact("audit@example.com");
        var member = SeedMember("member@test.com");
        await _dbContext.SaveChangesAsync();

        await _service.MergeContactToMemberAsync(contact, member, null, "test-merge");

        // Should have logged on both contact and member
        await _auditLogService.Received(2).LogAsync(
            AuditAction.ContactMergedToMember,
            nameof(User), Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private User SeedContact(string email, ContactSource source = ContactSource.Manual, string? externalId = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = email.Split('@')[0],
            UserName = email,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            AccountType = AccountType.Contact,
            ContactSource = source,
            ExternalSourceId = externalId,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private User SeedMember(string email)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = email.Split('@')[0],
            UserName = email,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            AccountType = AccountType.Member,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        return user;
    }
}
