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
/// Implementation of <see cref="IUserEmailBackfillService"/>. See interface
/// XML doc for context. The service is intentionally simple — at the
/// ~500-user scale this codebase targets, the orphan set is expected to be
/// near-zero or empty. The audit-log entry per insert is the operator-
/// visible artifact that confirms what happened.
/// </summary>
public sealed class UserEmailBackfillService : IUserEmailBackfillService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly IClock _clock;
    private readonly ILogger<UserEmailBackfillService> _logger;

    public UserEmailBackfillService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IFullProfileInvalidator fullProfileInvalidator,
        IClock clock,
        ILogger<UserEmailBackfillService> logger)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _fullProfileInvalidator = fullProfileInvalidator;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UserEmailBackfillResult> BackfillAsync(CancellationToken ct = default)
    {
        var orphans = await _userRepository.GetUsersWithoutUserEmailRowAsync(ct);
        var rowsInserted = 0;
        var skipped = new List<Guid>();
        var now = _clock.GetCurrentInstant();

        foreach (var user in orphans)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                skipped.Add(user.Id);
                _logger.LogWarning(
                    "UserEmailBackfill: user {UserId} has no User.Email to backfill from — skipping",
                    user.Id);
                continue;
            }

            var hasOAuthLogin = (await _userManager.GetLoginsAsync(user)).Count > 0;

            var userEmail = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Email = user.Email,
                IsVerified = user.EmailConfirmed,
                IsOAuth = hasOAuthLogin,
                IsNotificationTarget = user.EmailConfirmed,
                Visibility = ContactFieldVisibility.BoardOnly,
                DisplayOrder = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _userEmailRepository.AddAsync(userEmail, ct);
            rowsInserted++;

            // The insert succeeded — the user's UserEmail row is durably in
            // place. Both follow-on calls (cache invalidation and audit log)
            // are best-effort within this batch loop: per design-rules §7a
            // audit calls are best-effort by doctrine, and cache invalidation
            // in a batch loop follows the same policy so a single transient
            // failure doesn't abort an idempotent operator-triggered backfill
            // mid-iteration. Both failures are logged loudly so they're
            // visible in the operator's run.

            try
            {
                // FullProfile.EmailAddresses derives from user_emails; the
                // CachingProfileService dictionary holds a stale copy with
                // EmailAddresses=[] for this user until invalidated.
                await _fullProfileInvalidator.InvalidateAsync(user.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "UserEmailBackfill: FullProfile cache invalidation failed for user {UserId} — continuing batch",
                    user.Id);
            }

            try
            {
                // ContactCreated is a semantic approximation — there's no
                // dedicated UserEmailAdded / UserEmailBackfilled action
                // today, and adding one for a one-shot operation would
                // dilute the enum. The description string carries the
                // load-bearing operator-visible context.
                await _auditLogService.LogAsync(
                    AuditAction.ContactCreated,
                    nameof(User), user.Id,
                    $"Backfilled missing UserEmail row from User.Email = {user.Email} (verified={user.EmailConfirmed}, oauth={hasOAuthLogin})",
                    nameof(UserEmailBackfillService));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UserEmailBackfill: audit log failed for user {UserId} — best-effort, continuing batch",
                    user.Id);
            }

            _logger.LogInformation(
                "UserEmailBackfill: inserted UserEmail for user {UserId} ({Email})",
                user.Id, user.Email);
        }

        _logger.LogInformation(
            "UserEmailBackfill: complete — orphansFound={OrphansFound}, rowsInserted={RowsInserted}, skipped={Skipped}",
            orphans.Count, rowsInserted, skipped.Count);

        return new UserEmailBackfillResult(orphans.Count, rowsInserted, skipped);
    }
}
