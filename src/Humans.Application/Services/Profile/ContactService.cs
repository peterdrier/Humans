using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Manages external contacts as pre-provisioned Identity users.
/// Contacts have no credentials and LastLoginAt == null until they authenticate.
/// This is a coordination service — it doesn't own any table directly, but
/// orchestrates across User (Identity) and UserEmail (Profile) via their
/// owning service interfaces.
/// </summary>
public sealed class ContactService : IContactService
{
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IAuditLogService _auditLogService;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<ContactService> _logger;

    public ContactService(
        IUserService userService,
        IUserEmailService userEmailService,
        ICommunicationPreferenceService preferenceService,
        IAuditLogService auditLogService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ContactService> logger)
    {
        _userService = userService;
        _userEmailService = userEmailService;
        _preferenceService = preferenceService;
        _auditLogService = auditLogService;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
    }

    public async Task<User> CreateContactAsync(
        string email, string displayName, ContactSource source,
        string? externalSourceId = null, CancellationToken ct = default)
    {
        // Check for existing user by email/googleEmail (via IUserService)
        var existingUser = await _userService.GetByEmailOrAlternateAsync(email, ct);

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

        // Also check UserEmails table (via IUserEmailService)
        var existingUserEmail = await _userEmailService.FindVerifiedEmailWithUserAsync(email, ct);

        if (existingUserEmail is not null)
        {
            if (existingUserEmail.ContactSource is not null && existingUserEmail.LastLoginAt is null)
            {
                // Return the existing contact user
                return await _userService.GetByIdAsync(existingUserEmail.UserId, ct)
                    ?? throw new InvalidOperationException(
                        $"Contact user {existingUserEmail.UserId} found via email but missing from Users table.");
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

        // Create UserEmail record (via IUserEmailService)
        await _userEmailService.AddVerifiedEmailAsync(user.Id, email, ct);

        // Audit log
        await _auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), user.Id,
            $"Contact created from {source}{(externalSourceId is not null ? $" (ID: {externalSourceId})" : "")} — {email}",
            "ContactService");

        return user;
    }

    public async Task<IReadOnlyList<AdminContactRow>> GetFilteredContactsAsync(
        string? search, CancellationToken ct = default)
    {
        var contacts = await _userService.GetContactUsersAsync(search, ct);

        var contactIds = contacts.Select(c => c.Id).ToList();
        var usersWithPrefs = await _preferenceService.GetUsersWithAnyPreferencesAsync(contactIds, ct);

        var results = new List<AdminContactRow>(contacts.Count);
        foreach (var c in contacts)
        {
            results.Add(new AdminContactRow(
                c.Id, c.Email ?? string.Empty, c.DisplayName, c.ContactSource,
                c.ExternalSourceId, c.CreatedAt, usersWithPrefs.Contains(c.Id)));
        }

        return results;
    }

    public async Task<User?> GetContactDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);

        if (user is null || user.ContactSource is null || user.LastLoginAt is not null)
            return null;

        return user;
    }
}
