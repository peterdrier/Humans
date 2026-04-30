using Humans.Application.DTOs;
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
/// Google-email shadow column (or the legacy <c>IsOAuth=true</c> row when the
/// shadow value is null).
/// </para>
///
/// <para>
/// The legacy CLR properties (<c>User.GoogleEmail</c>, <c>UserEmail.IsOAuth</c>)
/// are gone; the columns survive on disk as EF shadow properties and are read
/// here through narrow repository projections
/// (<see cref="IUserRepository.GetLegacyGoogleEmailsAsync"/> and
/// <see cref="IUserEmailRepository.GetLegacyBackfillSnapshotsByUserIdAsync"/>).
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

        var legacyGoogleEmails = await _userRepository.GetLegacyGoogleEmailsAsync(
            users.Select(u => u.Id).ToArray(), cancellationToken);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            legacyGoogleEmails.TryGetValue(user.Id, out var legacyGoogleEmail);

            var snapshots = (await _userEmailRepository
                .GetLegacyBackfillSnapshotsByUserIdAsync(user.Id, cancellationToken))
                .ToList();
            if (snapshots.Count == 0)
                continue;

            var logins = await _userManager.GetLoginsAsync(user);
            var emails = (await _userEmailRepository.GetByUserIdForMutationAsync(user.Id, cancellationToken))
                .ToList();
            var updates = new List<UserEmail>();
            var taggedRowIds = new HashSet<Guid>();

            if (logins.Count > 1 && snapshots.Count == 1)
            {
                warnings.Add(
                    $"User {user.Id} has multiple AspNetUserLogins rows but only one UserEmail; first login wins.");
                ambiguousMatchesWarned++;
            }

            foreach (var login in logins)
            {
                var matchSnapshot = ResolveProviderTargetRow(
                    user, snapshots, login, legacyGoogleEmail);
                if (matchSnapshot is null)
                    continue;

                var match = emails.FirstOrDefault(e => e.Id == matchSnapshot.Id);
                if (match is null)
                    continue;

                if (string.Equals(match.Provider, login.LoginProvider, StringComparison.Ordinal)
                    && string.Equals(match.ProviderKey, login.ProviderKey, StringComparison.Ordinal))
                {
                    taggedRowIds.Add(match.Id);
                    continue;
                }

                if (taggedRowIds.Contains(match.Id))
                {
                    warnings.Add(
                        $"User {user.Id} login {login.LoginProvider}/{login.ProviderKey} mapped to UserEmail {match.Id} which is already tagged this run; skipped to avoid overwriting earlier login.");
                    ambiguousMatchesWarned++;
                    continue;
                }

                match.Provider = login.LoginProvider;
                match.ProviderKey = login.ProviderKey;
                match.UpdatedAt = now;
                taggedRowIds.Add(match.Id);
                if (!updates.Contains(match)) updates.Add(match);
                providerRowsUpdated++;

                await SafeAuditAsync(
                    user.Id,
                    $"Backfilled UserEmail.Provider/ProviderKey from AspNetUserLogins ({login.LoginProvider}) for {match.Email}");
            }

            var googleTargetSnapshot = ResolveIsGoogleTargetRow(snapshots, legacyGoogleEmail);
            if (googleTargetSnapshot is not null && !googleTargetSnapshot.IsGoogle)
            {
                var googleTarget = emails.FirstOrDefault(e => e.Id == googleTargetSnapshot.Id);
                if (googleTarget is not null)
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

    private static UserEmailLegacyBackfillSnapshot? ResolveProviderTargetRow(
        User user,
        IReadOnlyList<UserEmailLegacyBackfillSnapshot> emails,
        UserLoginInfo login,
        string? legacyGoogleEmail)
    {
        // 1. Legacy IsOAuth=true row whose Email matches the legacy GoogleEmail (most precise).
        if (!string.IsNullOrWhiteSpace(legacyGoogleEmail))
        {
            var byOAuthAndEmail = emails.FirstOrDefault(e =>
                e.LegacyIsOAuth
                && string.Equals(e.Email, legacyGoogleEmail, StringComparison.OrdinalIgnoreCase));
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

    private static UserEmailLegacyBackfillSnapshot? ResolveIsGoogleTargetRow(
        IReadOnlyList<UserEmailLegacyBackfillSnapshot> emails,
        string? legacyGoogleEmail)
    {
        // 1. Row matching the legacy GoogleEmail address.
        if (!string.IsNullOrWhiteSpace(legacyGoogleEmail))
        {
            var byGoogleEmail = emails.FirstOrDefault(e =>
                string.Equals(e.Email, legacyGoogleEmail, StringComparison.OrdinalIgnoreCase));
            if (byGoogleEmail is not null) return byGoogleEmail;
        }

        // 2. Legacy IsOAuth=true row (any user with an OAuth login pre-PR3 had this set).
        return emails.FirstOrDefault(e => e.LegacyIsOAuth);
    }

    private async Task SafeAuditAsync(Guid userId, string description)
    {
        try
        {
            await _auditLogService.LogAsync(
                AuditAction.UserEmailProviderBackfilled,
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
