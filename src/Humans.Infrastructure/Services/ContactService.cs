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
/// Manages external contacts as pre-provisioned Identity users.
/// Contacts have no credentials and LastLoginAt == null until they authenticate.
/// </summary>
public class ContactService : IContactService
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<ContactService> _logger;

    public ContactService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ICommunicationPreferenceService preferenceService,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<ContactService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _preferenceService = preferenceService;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<User> CreateContactAsync(
        string email, string displayName, ContactSource source,
        string? externalSourceId = null, CancellationToken ct = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);

        // Check for existing user with this email
        User? existingUser;
        if (alternateEmail is null)
        {
            existingUser = await _dbContext.Users
                .FirstOrDefaultAsync(u =>
                    (u.Email != null && EF.Functions.ILike(u.Email, normalizedEmail)) ||
                    (u.GoogleEmail != null && EF.Functions.ILike(u.GoogleEmail, normalizedEmail)),
                    ct);
        }
        else
        {
            existingUser = await _dbContext.Users
                .FirstOrDefaultAsync(u =>
                    (u.Email != null && (
                        EF.Functions.ILike(u.Email, normalizedEmail) ||
                        EF.Functions.ILike(u.Email, alternateEmail))) ||
                    (u.GoogleEmail != null && (
                        EF.Functions.ILike(u.GoogleEmail, normalizedEmail) ||
                        EF.Functions.ILike(u.GoogleEmail, alternateEmail))),
                    ct);
        }

        if (existingUser is not null)
        {
            if (existingUser.ContactSource is not null && existingUser.LastLoginAt is null)
            {
                _logger.LogDebug("Contact already exists for {Email}, returning existing", email);
                return existingUser;
            }

            throw new InvalidOperationException(
                $"An active account already exists for email {email}.");
        }

        // Also check UserEmails table
        UserEmail? existingUserEmail;
        if (alternateEmail is null)
        {
            existingUserEmail = await _dbContext.UserEmails
                .Include(ue => ue.User)
                .FirstOrDefaultAsync(ue => ue.IsVerified &&
                    EF.Functions.ILike(ue.Email, normalizedEmail), ct);
        }
        else
        {
            existingUserEmail = await _dbContext.UserEmails
                .Include(ue => ue.User)
                .FirstOrDefaultAsync(ue => ue.IsVerified &&
                    (EF.Functions.ILike(ue.Email, normalizedEmail) ||
                     EF.Functions.ILike(ue.Email, alternateEmail)), ct);
        }

        if (existingUserEmail is not null)
        {
            if (existingUserEmail.User.ContactSource is not null && existingUserEmail.User.LastLoginAt is null)
            {
                return existingUserEmail.User;
            }

            throw new InvalidOperationException(
                $"Email {email} is already verified on an existing account.");
        }

        var now = _clock.GetCurrentInstant();

        var user = new User
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            ContactSource = source,
            ExternalSourceId = externalSourceId,
            CreatedAt = now,
            EmailConfirmed = true
        };

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

        // Audit log
        await _auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), user.Id,
            $"Contact created from {source}{(externalSourceId is not null ? $" (ID: {externalSourceId})" : "")} — {email}",
            "ContactService");

        await _dbContext.SaveChangesAsync(ct);

        return user;
    }

    private static string? GetAlternateComparableEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
        {
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";
        }

        return null;
    }

    public async Task<IReadOnlyList<AdminContactRow>> GetFilteredContactsAsync(
        string? search, CancellationToken ct = default)
    {
        var query = _dbContext.Users
            .AsNoTracking()
            .Where(u => u.ContactSource != null && u.LastLoginAt == null);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.DisplayName, pattern) ||
                (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        var contacts = await query
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id,
                Email = u.Email ?? string.Empty,
                u.DisplayName,
                u.ContactSource,
                u.ExternalSourceId,
                u.CreatedAt,
            })
            .ToListAsync(ct);

        var results = new List<AdminContactRow>(contacts.Count);
        foreach (var c in contacts)
        {
            var hasPref = await _preferenceService.HasAnyPreferencesAsync(c.Id, ct);
            results.Add(new AdminContactRow(
                c.Id, c.Email, c.DisplayName, c.ContactSource,
                c.ExternalSourceId, c.CreatedAt, hasPref));
        }

        return results;
    }

    public async Task<User?> GetContactDetailAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u =>
                u.Id == userId &&
                u.ContactSource != null &&
                u.LastLoginAt == null, ct);
    }
}
