using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Base.Helpers;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Repositories;

internal sealed partial class UserRepository
{
    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdForMutationAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<UserEmail?> GetUserEmailByIdAndUserIdAsync(
        Guid emailId, Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == userId, ct);
    }

    public async Task<UserEmail?> GetUserEmailByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == emailId, ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByAddressAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default)
    {
        // ILIKE treats '_' and '%' as wildcards, so alex_smith@example.com would match
        // alexXsmith@example.com unescaped. Escape the pattern and use '\' as the escape.
        var escaped = EscapeLikePattern(normalizedEmail);
        var escapedAlternate = alternateEmail is null ? null : EscapeLikePattern(alternateEmail);

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = ctx.UserEmails.AsNoTracking();
        rows = escapedAlternate is null
            ? rows.Where(e => EF.Functions.ILike(e.Email, escaped, "\\"))
            : rows.Where(e => EF.Functions.ILike(e.Email, escaped, "\\") ||
                              EF.Functions.ILike(e.Email, escapedAlternate, "\\"));
        return await rows.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetAllUserEmailsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsHavingAnyUserEmailAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => userIds.Contains(e.UserId))
            .Select(e => e.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task RemoveAllUserEmailsForUserAndSaveAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var emails = await ctx.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);

        if (emails.Count == 0)
            return;

        ctx.UserEmails.RemoveRange(emails);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveAllUserEmailsForUsersAndSaveAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.UserEmails
            .Where(e => userIds.Contains(e.UserId))
            .ExecuteDeleteAsync(ct);
    }

    public async Task ReassignUserEmailsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceRows = await ctx.UserEmails
            .Where(e => e.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetRows = await ctx.UserEmails
            .Where(e => e.UserId == targetUserId)
            .ToListAsync(ct);

        // Lower-cased defensively: Email is normalized on write, but a legacy mixed-case row may exist.
        var targetByAddress = targetRows
            .ToDictionary(e => e.Email, StringComparer.OrdinalIgnoreCase);

        foreach (var src in sourceRows)
        {
            if (targetByAddress.TryGetValue(src.Email, out var tgt))
            {
                if (src.IsVerified)
                {
                    tgt.IsVerified = true;
                }
                tgt.UpdatedAt = updatedAt;
                ctx.UserEmails.Remove(src);
            }
            else
            {
                // Clear IsPrimary and IsGoogle so the target's selections stand; the frozen IsGoogle
                // designation is what the google.workspace-identities fallback reads.
                ctx.Entry(src).Property(nameof(UserEmail.UserId)).CurrentValue = targetUserId;
                src.IsPrimary = false;
                src.IsGoogle = false;
                src.UpdatedAt = updatedAt;
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> MarkUserEmailVerifiedAsync(
        Guid emailId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var email = await ctx.UserEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null)
            return false;

        email.IsVerified = true;
        email.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveUserEmailByIdAsync(Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var email = await ctx.UserEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null)
            return false;

        ctx.UserEmails.Remove(email);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default)
    {
        if (emails.Count == 0)
            return [];

        // The email directory fits in RAM. Use the same identity comparer as the Google callers,
        // preserving exact matches before Gmail dot/plus/domain aliases without SQL casing rules.
        var exactTargets = new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase);
        var canonicalTargets = new HashSet<string>(emails, GmailAliasEmailComparer.Instance);
        var exact = new List<UserEmail>();
        var aliases = new List<UserEmail>();
        foreach (var row in await GetAllUserEmailsAsync(ct))
        {
            if (exactTargets.Contains(row.Email))
                exact.Add(row);
            else if (canonicalTargets.Contains(row.Email))
                aliases.Add(row);
        }
        return [.. exact, .. aliases];
    }

    public async Task<Dictionary<Guid, string>> GetAllNotificationTargetUserEmailsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => e.IsVerified && e.IsPrimary)
            .GroupBy(e => e.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Email = g.Select(e => e.Email).First()
            })
            .ToDictionaryAsync(x => x.UserId, x => x.Email, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsByUserEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(ue => ue.Email.StartsWith(prefix) && ue.Email.EndsWith(suffix))
            .Select(ue => ue.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task AddUserEmailAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.UserEmails.Add(email);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveUserEmailAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(email);
        ctx.UserEmails.Remove(email);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateUserEmailAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(email);
        ctx.Entry(email).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateUserEmailsAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        foreach (var email in emails)
        {
            ctx.Attach(email);
            ctx.Entry(email).State = EntityState.Modified;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ApplyUserEmailReconcilePlanAsync(
        UserEmail? displacedRowToDelete,
        UserEmail? rowToDelete,
        UserEmail? rowToUpdate,
        UserEmail? rowToInsert,
        CancellationToken ct = default)
    {
        if (displacedRowToDelete is null && rowToDelete is null
            && rowToUpdate is null && rowToInsert is null)
            return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);

        if (displacedRowToDelete is not null)
        {
            ctx.Attach(displacedRowToDelete);
            ctx.UserEmails.Remove(displacedRowToDelete);
        }

        if (rowToDelete is not null)
        {
            ctx.Attach(rowToDelete);
            ctx.UserEmails.Remove(rowToDelete);
        }

        if (rowToUpdate is not null)
        {
            ctx.Attach(rowToUpdate);
            ctx.Entry(rowToUpdate).State = EntityState.Modified;
        }

        if (rowToInsert is not null)
        {
            ctx.UserEmails.Add(rowToInsert);
        }

        try
        {
            await ctx.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException dbex)
            when (dbex.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_user_emails_Email" })
        {
            // The verified-email unique index caught a concurrent cross-user insert. Translated
            // here so callers never see Npgsql types; the sign-in callers swallow and log it so
            // sign-in never blocks.
            throw new OAuthReconcileConcurrencyException(
                "OAuth reconcile race: a concurrent insert violated the " +
                "verified-email partial unique index (Postgres 23505).",
                dbex);
        }
    }
}
