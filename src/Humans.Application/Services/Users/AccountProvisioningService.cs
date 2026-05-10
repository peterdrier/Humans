using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

/// <summary>
/// Idempotent account provisioning for import jobs (ticket import, MailerLite
/// import). Looks up existing users by email across all <see cref="UserEmail"/>
/// records and creates a new User + UserEmail when no match exists.
/// </summary>
/// <remarks>
/// Application-layer implementation — goes through <see cref="IUserRepository"/>
/// for User reads/writes and <see cref="IUserEmailService"/> for UserEmail
/// row creation (issue nobodies-collective/Humans#687: every UserEmail-add
/// path must go through the orchestrator that runs the Primary + Google
/// invariants). Never injects <c>DbContext</c>.
/// <para>
/// User creation itself still goes through <see cref="UserManager{TUser}"/>
/// because ASP.NET Identity owns the password hashing / concurrency stamp /
/// security stamp fields. UserManager is a framework abstraction and is
/// allowed in the Application layer (see design-rules §2a exception).
/// </para>
/// </remarks>
public sealed class AccountProvisioningService : IAccountProvisioningService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserEmailService _userEmailService;
    private readonly IProfileService _profileService;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<AccountProvisioningService> _logger;

    public AccountProvisioningService(
        IUserRepository userRepository,
        IUserEmailService userEmailService,
        IProfileService profileService,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<AccountProvisioningService> logger)
    {
        _userRepository = userRepository;
        _userEmailService = userEmailService;
        _profileService = profileService;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AccountProvisioningResult> FindOrCreateUserByEmailAsync(
        string email, string? displayName, ContactSource source,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        // 1. Check UserEmail records (includes OAuth, verified, and unverified
        //    emails). Issue nobodies-collective/Humans#687: routed through
        //    IUserEmailService rather than IUserEmailRepository so the orchestrator
        //    + invariants own all UserEmail mutations.
        var matchingUserId = await _userEmailService.FindAnyUserIdByEmailAsync(email, ct);

        if (matchingUserId is not null)
        {
            var existingUser = await _userRepository.GetByIdAsync(matchingUserId.Value, ct);
            if (existingUser is null)
            {
                _logger.LogWarning(
                    "Orphan UserEmail references missing user {UserId} during lookup for {Email}",
                    matchingUserId.Value, email);
            }
            else
            {
                _logger.LogDebug(
                    "Found existing account {UserId} via UserEmail match for {Email} (source: {Source})",
                    existingUser.Id, email, source);

                // Layer the ContactSource if the user was self-registered (no source yet)
                if (existingUser.ContactSource is null)
                {
                    await _userRepository.SetContactSourceIfNullAsync(existingUser.Id, source, ct);
                    existingUser.ContactSource = source;
                }

                return new AccountProvisioningResult(existingUser, Created: false);
            }
        }

        // No match found — create new User + UserEmail.
        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? email.Split('@')[0]
            : displayName;

        var now = _clock.GetCurrentInstant();

        var newUserId = Guid.NewGuid();
        var newUser = new User
        {
            Id = newUserId,
            DisplayName = resolvedDisplayName,
            ContactSource = source,
            CreatedAt = now,
        };

        // UserManager persists the user (its Identity store calls SaveChanges).
        var result = await _userManager.CreateAsync(newUser);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create account for {email}: {errors}");
        }

        // Issue nobodies-collective/Humans#687: route the UserEmail row creation
        // through the orchestrator on IUserEmailService instead of writing to
        // IUserEmailRepository directly. The orchestrator runs
        // EnsurePrimaryInvariantAsync + EnsureGoogleInvariantAsync so the new
        // user gets exactly one IsPrimary row and exactly one IsGoogle row.
        await _userEmailService.AddProvisionedEmailAsync(newUser.Id, email, ct);

        // Issue #635 (§15i): Stub Profile invariant. Every newly provisioned
        // user gets a Profile row in the Stub state so cross-section reads
        // (Profile.PrimaryEmail, Profile.GoogleEmail, etc.) never have to
        // null-check the Profile pointer. Transitions to Active happen
        // when ProfileService.SaveProfileAsync sees required fields populate.
        // Goes through IProfileService (not IProfileRepository) per design-rules
        // §2c: cross-section writes flow through the owning section's service.
        await _profileService.EnsureStubProfileAsync(newUser.Id, ct);

        await _auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), newUser.Id,
            $"Account pre-created from {source} — {email}",
            nameof(AccountProvisioningService));

        _logger.LogInformation(
            "Created new account {UserId} for {Email} (source: {Source}, displayName: {DisplayName})",
            newUser.Id, email, source, resolvedDisplayName);

        return new AccountProvisioningResult(newUser, Created: true);
    }
}
