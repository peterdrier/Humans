using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Manages lightweight external contacts imported from MailerLite, TicketTailor, or manual entry.
/// Contacts have communication preferences but cannot log in or access the platform.
/// </summary>
public class ContactService : IContactService
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly ICommunicationPreferenceService _communicationPreferenceService;
    private readonly IClock _clock;
    private readonly ILogger<ContactService> _logger;

    public ContactService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        ICommunicationPreferenceService communicationPreferenceService,
        IClock clock,
        ILogger<ContactService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _communicationPreferenceService = communicationPreferenceService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<User> CreateContactAsync(
        string email, string displayName, ContactSource source,
        string? externalSourceId = null, CancellationToken ct = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);

        // Check for existing user with this email
        var existingUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail.ToUpperInvariant(), ct);

        if (existingUser is not null)
        {
            if (existingUser.AccountType == AccountType.Contact)
            {
                _logger.LogDebug("Contact already exists for {Email}, returning existing", email);
                return existingUser;
            }

            throw new InvalidOperationException(
                $"A member account already exists for email {email}. Use the account merge flow instead.");
        }

        // Also check UserEmails table for the email on any existing account
        var existingUserEmail = await _dbContext.UserEmails
            .Include(ue => ue.User)
            .FirstOrDefaultAsync(ue => ue.IsVerified &&
                EF.Functions.ILike(ue.Email, email), ct);

        if (existingUserEmail is not null)
        {
            if (existingUserEmail.User.AccountType == AccountType.Contact)
            {
                return existingUserEmail.User;
            }

            throw new InvalidOperationException(
                $"Email {email} is already verified on member account {existingUserEmail.UserId}.");
        }

        var now = _clock.GetCurrentInstant();

        var user = new User
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            AccountType = AccountType.Contact,
            ContactSource = source,
            ExternalSourceId = externalSourceId,
            CreatedAt = now,
            EmailConfirmed = true
        };

        // Disable login for contacts
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create contact: {errors}");
        }

        // Create UserEmail record
        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = email,
            IsOAuth = false,
            IsVerified = true,
            IsNotificationTarget = true,
            Visibility = null,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.UserEmails.Add(userEmail);

        // Set default communication preferences: Marketing opted-in (the reason contacts exist),
        // EventOperations opted-out (not a member)
        await _communicationPreferenceService.UpdatePreferenceAsync(
            user.Id, MessageCategory.Marketing, optedOut: false, "ContactCreation", ct);
        await _communicationPreferenceService.UpdatePreferenceAsync(
            user.Id, MessageCategory.EventOperations, optedOut: true, "ContactCreation", ct);

        // Audit log
        await _auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), user.Id,
            $"Contact created from {source}{(externalSourceId is not null ? $" (ID: {externalSourceId})" : "")} — {email}",
            "ContactService");

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created contact {UserId} for {Email} from {Source}",
            user.Id, email, source);

        return user;
    }

    public async Task<User?> FindContactByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email).ToUpperInvariant();

        return await _dbContext.Users
            .FirstOrDefaultAsync(u =>
                u.AccountType == AccountType.Contact &&
                u.NormalizedEmail == normalizedEmail, ct);
    }

    public async Task<User?> FindContactByExternalIdAsync(
        ContactSource source, string externalSourceId, CancellationToken ct = default)
    {
        return await _dbContext.Users
            .FirstOrDefaultAsync(u =>
                u.AccountType == AccountType.Contact &&
                u.ContactSource == source &&
                u.ExternalSourceId == externalSourceId, ct);
    }

    public async Task<IReadOnlyList<AdminContactRow>> GetFilteredContactsAsync(
        string? search, CancellationToken ct = default)
    {
        var query = _dbContext.Users
            .AsNoTracking()
            .Where(u => u.AccountType == AccountType.Contact);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.DisplayName, pattern) ||
                (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        return await query
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new AdminContactRow(
                u.Id,
                u.Email ?? string.Empty,
                u.DisplayName,
                u.ContactSource,
                u.ExternalSourceId,
                u.CreatedAt.ToDateTimeUtc(),
                u.CommunicationPreferences.Any()))
            .ToListAsync(ct);
    }

    public async Task<User?> GetContactDetailAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.Users
            .Include(u => u.CommunicationPreferences)
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u =>
                u.Id == userId &&
                u.AccountType == AccountType.Contact, ct);
    }

    public async Task MergeContactToMemberAsync(
        User contactUser, User memberUser,
        Guid? actorUserId, string actorName, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Merging contact {ContactId} ({ContactEmail}) into member {MemberId} ({MemberEmail})",
            contactUser.Id, contactUser.Email, memberUser.Id, memberUser.Email);

        // 1. Migrate communication preferences (member's existing preferences win)
        var contactPrefs = await _dbContext.CommunicationPreferences
            .Where(cp => cp.UserId == contactUser.Id)
            .ToListAsync(ct);

        var memberPrefCategories = await _dbContext.CommunicationPreferences
            .Where(cp => cp.UserId == memberUser.Id)
            .Select(cp => cp.Category)
            .ToListAsync(ct);

        foreach (var pref in contactPrefs)
        {
            if (!memberPrefCategories.Contains(pref.Category))
            {
                // Member doesn't have this category — create it on the member
                _dbContext.CommunicationPreferences.Add(new CommunicationPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = memberUser.Id,
                    Category = pref.Category,
                    OptedOut = pref.OptedOut,
                    UpdatedAt = _clock.GetCurrentInstant(),
                    UpdateSource = "ContactMerge",
                    User = memberUser
                });
            }

            // Remove the contact's preference regardless
            _dbContext.CommunicationPreferences.Remove(pref);
        }

        // 2. Migrate UserEmails that the member doesn't already have
        var contactEmails = await _dbContext.UserEmails
            .Where(e => e.UserId == contactUser.Id)
            .ToListAsync(ct);

        var memberEmailAddresses = await _dbContext.UserEmails
            .Where(e => e.UserId == memberUser.Id)
            .Select(e => e.Email.ToLowerInvariant())
            .ToListAsync(ct);

        var memberEmailSet = new HashSet<string>(memberEmailAddresses, StringComparer.OrdinalIgnoreCase);

        foreach (var contactEmail in contactEmails)
        {
            // Remove the contact's email record; if the member doesn't have it, re-create on their account
            _dbContext.UserEmails.Remove(contactEmail);

            if (!memberEmailSet.Contains(contactEmail.Email))
            {
                _dbContext.UserEmails.Add(new UserEmail
                {
                    Id = Guid.NewGuid(),
                    UserId = memberUser.Id,
                    Email = contactEmail.Email,
                    IsOAuth = false,
                    IsVerified = contactEmail.IsVerified,
                    IsNotificationTarget = false,
                    Visibility = contactEmail.Visibility,
                    DisplayOrder = contactEmail.DisplayOrder,
                    CreatedAt = contactEmail.CreatedAt,
                    UpdatedAt = _clock.GetCurrentInstant()
                });
            }
        }

        // 3. Deactivate the contact (don't delete — preserve for audit trail)
        contactUser.AccountType = AccountType.Deactivated;
        contactUser.LockoutEnabled = true;
        contactUser.LockoutEnd = DateTimeOffset.MaxValue;

        // 4. Audit log on both accounts
        if (actorUserId.HasValue)
        {
            await _auditLogService.LogAsync(
                AuditAction.ContactMergedToMember,
                nameof(User), contactUser.Id,
                $"Contact merged into member {memberUser.DisplayName} ({memberUser.Email})",
                actorUserId.Value, actorName,
                relatedEntityId: memberUser.Id, relatedEntityType: nameof(User));

            await _auditLogService.LogAsync(
                AuditAction.ContactMergedToMember,
                nameof(User), memberUser.Id,
                $"Absorbed contact {contactUser.Email} from {contactUser.ContactSource}",
                actorUserId.Value, actorName,
                relatedEntityId: contactUser.Id, relatedEntityType: nameof(User));
        }
        else
        {
            await _auditLogService.LogAsync(
                AuditAction.ContactMergedToMember,
                nameof(User), contactUser.Id,
                $"Contact merged into member {memberUser.DisplayName} ({memberUser.Email})",
                actorName,
                relatedEntityId: memberUser.Id, relatedEntityType: nameof(User));

            await _auditLogService.LogAsync(
                AuditAction.ContactMergedToMember,
                nameof(User), memberUser.Id,
                $"Absorbed contact {contactUser.Email} from {contactUser.ContactSource}",
                actorName,
                relatedEntityId: contactUser.Id, relatedEntityType: nameof(User));
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Contact {ContactId} merged into member {MemberId}",
            contactUser.Id, memberUser.Id);
    }
}
