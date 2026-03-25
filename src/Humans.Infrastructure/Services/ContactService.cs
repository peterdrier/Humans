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
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<ContactService> _logger;

    public ContactService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<ContactService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _auditLogService = auditLogService;
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
            if (existingUser.ContactSource is not null && existingUser.LastLoginAt is null)
            {
                _logger.LogDebug("Contact already exists for {Email}, returning existing", email);
                return existingUser;
            }

            throw new InvalidOperationException(
                $"An active account already exists for email {email}.");
        }

        // Also check UserEmails table
        var existingUserEmail = await _dbContext.UserEmails
            .Include(ue => ue.User)
            .FirstOrDefaultAsync(ue => ue.IsVerified &&
                EF.Functions.ILike(ue.Email, email), ct);

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

        _logger.LogInformation(
            "Created contact {UserId} for {Email} from {Source}",
            user.Id, email, source);

        return user;
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

        return await query
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new AdminContactRow(
                u.Id,
                u.Email ?? string.Empty,
                u.DisplayName,
                u.ContactSource,
                u.ExternalSourceId,
                u.CreatedAt,
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
                u.ContactSource != null &&
                u.LastLoginAt == null, ct);
    }
}
