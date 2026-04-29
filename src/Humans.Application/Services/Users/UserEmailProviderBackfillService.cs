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
/// Implementation of <see cref="IUserEmailProviderBackfillService"/>. See
/// interface XML doc for context.
///
/// <para>
/// At ~500-user scale a full table scan is trivial. The service loads every
/// user (with their <c>UserEmails</c> via <see cref="IUserRepository.GetAllAsync"/>)
/// and every user's <c>AspNetUserLogins</c> rows via the existing
/// <see cref="UserManager{TUser}.GetLoginsAsync"/> contract. For each user it
/// (a) tags the matching <see cref="UserEmail"/> row with
/// <c>Provider</c>/<c>ProviderKey</c> for each <c>AspNetUserLogins</c> entry
/// and (b) flips <c>IsGoogle = true</c> on the row matching the legacy
/// <see cref="User.GoogleEmail"/> address (or the legacy
/// <see cref="UserEmail.IsOAuth"/> row when <c>GoogleEmail</c> is null).
/// </para>
///
/// <para>
/// Reads of the legacy properties (<c>User.GoogleEmail</c>,
/// <c>UserEmail.IsOAuth</c>) are intentional here — the C# surface still
/// exists in this PR and is deleted in Task 10 of the plan. After that
/// deletion this service switches to <c>EF.Property&lt;&gt;(...)</c> reads
/// against the EF shadow-property declarations introduced in
/// <see cref="Humans.Infrastructure.Data.Configurations.Profiles"/>.
/// </para>
/// </summary>
public sealed class UserEmailProviderBackfillService : IUserEmailProviderBackfillService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<UserEmailProviderBackfillService> _logger;

    public UserEmailProviderBackfillService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<UserEmailProviderBackfillService> logger)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UserEmailProviderBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var users = await _userRepository.GetAllAsync(cancellationToken);
        var warnings = new List<string>();
        var providerRowsUpdated = 0;
        var isGoogleRowsUpdated = 0;
        var ambiguousMatchesWarned = 0;
        var now = _clock.GetCurrentInstant();

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var emails = (await _userEmailRepository.GetByUserIdForMutationAsync(user.Id, cancellationToken))
                .ToList();
            if (emails.Count == 0)
                continue;

            var logins = await _userManager.GetLoginsAsync(user);
            var updates = new List<UserEmail>();

            foreach (var login in logins)
            {
                var match = ResolveProviderTargetRow(
                    user, emails, login, logins.Count > 1, warnings, ref ambiguousMatchesWarned);
                if (match is null)
                    continue;

                if (string.Equals(match.Provider, login.LoginProvider, StringComparison.Ordinal)
                    && string.Equals(match.ProviderKey, login.ProviderKey, StringComparison.Ordinal))
                {
                    continue;
                }

                match.Provider = login.LoginProvider;
                match.ProviderKey = login.ProviderKey;
                match.UpdatedAt = now;
                if (!updates.Contains(match)) updates.Add(match);
                providerRowsUpdated++;

                await SafeAuditAsync(
                    user.Id,
                    $"Backfilled UserEmail.Provider/ProviderKey from AspNetUserLogins ({login.LoginProvider}) for {match.Email}");
            }

            var googleTarget = ResolveIsGoogleTargetRow(user, emails);
            if (googleTarget is not null && !googleTarget.IsGoogle)
            {
                foreach (var sibling in emails)
                {
                    if (sibling.Id == googleTarget.Id) continue;
                    if (!sibling.IsGoogle) continue;
                    sibling.IsGoogle = false;
                    sibling.UpdatedAt = now;
                    if (!updates.Contains(sibling)) updates.Add(sibling);
                }

                googleTarget.IsGoogle = true;
                googleTarget.UpdatedAt = now;
                if (!updates.Contains(googleTarget)) updates.Add(googleTarget);
                isGoogleRowsUpdated++;

                await SafeAuditAsync(
                    user.Id,
                    $"Backfilled UserEmail.IsGoogle on {googleTarget.Email}");
            }

            if (updates.Count > 0)
                await _userEmailRepository.UpdateBatchAsync(updates, cancellationToken);
        }

        _logger.LogInformation(
            "UserEmailProviderBackfill: complete — usersProcessed={UsersProcessed}, providerRowsUpdated={ProviderRowsUpdated}, isGoogleRowsUpdated={IsGoogleRowsUpdated}, ambiguous={Ambiguous}",
            users.Count, providerRowsUpdated, isGoogleRowsUpdated, ambiguousMatchesWarned);

        return new UserEmailProviderBackfillResult(
            users.Count, providerRowsUpdated, isGoogleRowsUpdated, ambiguousMatchesWarned, warnings);
    }

    private static UserEmail? ResolveProviderTargetRow(
        User user,
        IReadOnlyList<UserEmail> emails,
        UserLoginInfo login,
        bool ambiguous,
        List<string> warnings,
        ref int ambiguousMatchesWarned)
    {
        if (ambiguous && emails.Count == 1)
        {
            warnings.Add(
                $"User {user.Id} has multiple AspNetUserLogins rows but only one UserEmail; first-match wins (login={login.LoginProvider}/{login.ProviderKey}).");
            ambiguousMatchesWarned++;
        }

        // 1. Legacy IsOAuth=true row whose Email matches User.GoogleEmail (most precise).
        if (!string.IsNullOrWhiteSpace(user.GoogleEmail))
        {
            var byOAuthAndEmail = emails.FirstOrDefault(e =>
                e.IsOAuth
                && string.Equals(e.Email, user.GoogleEmail, StringComparison.OrdinalIgnoreCase));
            if (byOAuthAndEmail is not null) return byOAuthAndEmail;
        }

        // 2. Row matching the User.Email override (resolved via UserEmails by the override).
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            var byUserEmail = emails.FirstOrDefault(e =>
                string.Equals(e.Email, user.Email, StringComparison.OrdinalIgnoreCase));
            if (byUserEmail is not null) return byUserEmail;
        }

        // 3. First row alphabetically (deterministic fallback).
        return emails.OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static UserEmail? ResolveIsGoogleTargetRow(User user, IReadOnlyList<UserEmail> emails)
    {
        // 1. Row matching the legacy User.GoogleEmail address.
        if (!string.IsNullOrWhiteSpace(user.GoogleEmail))
        {
            var byGoogleEmail = emails.FirstOrDefault(e =>
                string.Equals(e.Email, user.GoogleEmail, StringComparison.OrdinalIgnoreCase));
            if (byGoogleEmail is not null) return byGoogleEmail;
        }

        // 2. Legacy IsOAuth=true row (any user with an OAuth login pre-PR3 had this set).
        return emails.FirstOrDefault(e => e.IsOAuth);
    }

    private async Task SafeAuditAsync(Guid userId, string description)
    {
        try
        {
            await _auditLogService.LogAsync(
                AuditAction.ContactCreated,
                nameof(User), userId,
                description,
                nameof(UserEmailProviderBackfillService));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "UserEmailProviderBackfill: audit log failed for user {UserId} — best-effort, continuing batch",
                userId);
        }
    }
}
