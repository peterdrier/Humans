using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

internal sealed class CreditorBindings(
    IFinanceRepository repo,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IClock clock,
    IAuditLogService audit,
    ILogger<Service> logger) : IApplicationService
{
    internal const string HoldedCreditorAccount = "HoldedCreditorAccount";

    // A new expense-only contact gets type "creditor", which Holded numbers in the 410 series rather than the
    // 400-series proveedor accounts older members carry, so the block spans both. Bindings, not the range, tell a
    // member's account from an org vendor's.
    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 41999999;

    internal static bool IsMemberCreditorAccount(int? accountNum) =>
        accountNum is >= CreditorAccountMin and <= CreditorAccountMax;

    internal async Task<CreditorContactBinding?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var b = await repo.GetCreditorContactByUserAsync(userId, ct);
        return b is null ? null : ToBinding(b);
    }

    internal static CreditorContactBinding ToBinding(HoldedCreditorContact b) =>
        new(b.UserId, b.HoldedContactId, b.SupplierAccountNum, b.Source) { CreatedAt = b.CreatedAt };

    // An account, and the Holded contact behind it, binds to at most one member. All three write paths check with
    // FindConflictingBinding and differ only in the remedy: refuse when the value is our guess, write and let the
    // collision show when it is Holded's own word. Not a unique DB index; see Docs/health.md.

    /// <summary>Overlap on either the 400000xx or the contact merges two members' payables, so both are conflicts.</summary>
    private static HoldedCreditorContact? FindConflictingBinding(
        IEnumerable<HoldedCreditorContact> bindings,
        Guid userId,
        int? supplierAccountNum,
        string? holdedContactId) =>
        bindings.FirstOrDefault(b =>
            b.UserId != userId
            && ((supplierAccountNum is not null && b.SupplierAccountNum == supplierAccountNum)
                || (!string.IsNullOrEmpty(holdedContactId)
                    && string.Equals(b.HoldedContactId, holdedContactId, StringComparison.Ordinal))));

    /// <summary>The admin sees the collision as a duplicate row on /Finance/Creditors; this is the trail of how it
    /// arrived.</summary>
    private void LogBindingCollision(
        string writePath, Guid userId, string holdedContactId, int? supplierAccountNum,
        HoldedCreditorContact conflict) =>
        logger.LogError(
            "Creditor binding collision in {WritePath}: member {UserId} resolved to Holded contact " +
            "{HoldedContactId} / account {SupplierAccountNum}, which is already bound to member " +
            "{ConflictUserId} ({ConflictSource}). Holded is authoritative so the binding was written; " +
            "both now show on /Finance/Creditors and one must be unbound.",
            writePath, userId, holdedContactId, supplierAccountNum, conflict.UserId, conflict.Source);

    /// <summary>Still exactly <paramref name="asRead"/>: same row, same content, or still absent. Compared field by
    /// field: the upsert is keyed by UserId and mutates in place, so an admin's rebind keeps the Id.</summary>
    private async Task<bool> BindingUnchangedAsync(
        string writePath, Guid userId, HoldedCreditorContact? asRead, CancellationToken ct)
    {
        var current = await repo.GetCreditorContactByUserAsync(userId, ct);
        if (current is null && asRead is null) return true;
        if (current is not null && asRead is not null
            && current.Id == asRead.Id
            && string.Equals(current.HoldedContactId, asRead.HoldedContactId, StringComparison.Ordinal)
            && current.SupplierAccountNum == asRead.SupplierAccountNum
            && current.Source == asRead.Source)
            return true;

        // The admin's action stands and nothing is written, so this is the only record that a push was overtaken.
        logger.LogWarning(
            "Skipped the creditor binding write in {WritePath} for member {UserId}: the binding changed " +
            "while the push was in flight — was {WasContactId} / {WasAccountNum} ({WasSource}), is now " +
            "{NowContactId} / {NowAccountNum} ({NowSource}). The newer binding stands.",
            writePath, userId,
            asRead?.HoldedContactId, asRead?.SupplierAccountNum, asRead?.Source,
            current?.HoldedContactId, current?.SupplierAccountNum, current?.Source);
        return false;
    }

    internal async Task<CreditorBindResult> SetCreditorContactAsync(
        Guid userId, int supplierAccountNum, Guid actorUserId, CancellationToken ct = default)
    {
        // The dropdown is filtered, but the number arrives on a POST. Holded numbers every supplier contact, so without
        // this an org vendor's account is bindable.
        if (!IsMemberCreditorAccount(supplierAccountNum))
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is outside the member creditor block " +
                $"({CreditorAccountMin}–{CreditorAccountMax}) — that is not a member's account.");

        // Only UserId is unique in the DB. Checked before the Holded call so a doomed bind costs no round-trip.
        var bindings = await repo.GetCreditorContactsAsync(ct);
        if (FindConflictingBinding(bindings, userId, supplierAccountNum, holdedContactId: null) is not null)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is already bound to a different member. " +
                "Check /Finance/Creditors to see who, and unbind them there first.");

        // Refreshed first, so a contact made in Holded since the last sync binds, and the rows read it afterwards.
        await holded.SyncContactsAsync(ct);
        var contact = (await holded.GetContactsAsync(ct)).Contacts
            .FirstOrDefault(c => c.SupplierAccountNum == supplierAccountNum);
        if (contact is null)
            return CreditorBindResult.Failure(
                $"No Holded contact carries account {supplierAccountNum} — nothing bound.");

        // A member whose binding carries this contact but whose 400000xx never resolved is invisible to the check above
        // and on /Finance/Creditors (keyed by number), yet merges payables just the same. So name the contact.
        if (FindConflictingBinding(bindings, userId, supplierAccountNum: null, contact.Id) is not null)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} belongs to Holded contact \"{contact.Name}\", which is " +
                "already bound to a different member whose account number has not resolved yet. " +
                "Nothing was changed — two members must never share one Holded contact.");

        var now = clock.GetCurrentInstant();
        if (!await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contact.Id,
            SupplierAccountNum = supplierAccountNum,
            Source = CreditorContactSource.Manual,
            CreatedAt = now,
            UpdatedAt = now,
        }, now, ct))
            return CreditorBindResult.Failure(
                "This member was bound by a concurrent action while you were binding. Reload and check " +
                "/Finance/Creditors.");
        await audit.LogAsync(FinanceAuditActions.HoldedCreditorBound, HoldedCreditorAccount, userId,
            $"Bound creditor account {supplierAccountNum} (Holded contact {contact.Id})",
            actorUserId, userId, AuditSubjectTypes.User);
        return CreditorBindResult.Success;
    }

    internal async Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct = default)
    {
        var contactId = await EnsureCreditorContactCoreAsync(
            userId, legalName, burnerName, iban, seedContactId, seedAccountNum, jobName, ct);
        await EnsureContactMirroredAsync(contactId, accountNum: null, ct);
        return contactId;
    }

    /// <summary>A contact a push minted or numbered must be in the mirror before the next nightly sync, or recon and
    /// /Finance/Creditors call its binding missing. One list call, and only when the mirror lacks it. A failure is
    /// logged, not thrown: the push already succeeded, and the nightly sync catches up.</summary>
    private async Task EnsureContactMirroredAsync(string contactId, int? accountNum, CancellationToken ct)
    {
        var mirrored = (await holded.GetContactsAsync(ct)).Contacts
            .FirstOrDefault(c => string.Equals(c.Id, contactId, StringComparison.Ordinal));
        if (mirrored is not null && (accountNum is null || mirrored.SupplierAccountNum == accountNum)) return;
        try
        {
            await holded.SyncContactsAsync(ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(
                "Holded contact {ContactId} is not in the contact mirror and the refresh failed; it appears after the "
                + "next sync. {Reason}", contactId, ex.Message);
        }
    }

    private async Task<string> EnsureCreditorContactCoreAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        var allBindings = await repo.GetCreditorContactsAsync(ct);

        // The seed is our guess off a prior report, not Holded's word, so it gets the manual bind's remedy: refuse and
        // let the push mint a contact. That is also what makes Unbind durable.
        if (binding is null
            && FindConflictingBinding(allBindings, userId, seedAccountNum, seedContactId) is { } seedConflict)
        {
            logger.LogError(
                "Refused a creditor seed for member {UserId}: the contact {SeedContactId} / account " +
                "{SeedAccountNum} cached on their prior report is bound to member {ConflictUserId} " +
                "({ConflictSource}). Adopting it would merge their payables, so a new Holded contact " +
                "is being created instead.",
                userId, seedContactId, seedAccountNum, seedConflict.UserId, seedConflict.Source);
            seedContactId = null;
            seedAccountNum = null;
        }

        // Never PUT a linked contact: Holded's v2 update is a full replacement, so supplier_record resets and the next
        // purchase doc mints a second creditor account. So a legal-name or IBAN change after the first push does not
        // reach Holded (open: peterdrier/Humans#1777).
        string contactId;
        var minted = false;
        if (!string.IsNullOrEmpty(binding?.HoldedContactId))
            contactId = binding.HoldedContactId;
        else if (!string.IsNullOrEmpty(seedContactId))
            contactId = seedContactId;
        else
        {
            // Burner goes in tradeName only, and only when it differs from the legal name.
            var tradeName = !string.IsNullOrWhiteSpace(burnerName)
                            && !string.Equals(burnerName, legalName, StringComparison.Ordinal)
                ? burnerName
                : null;

            contactId = await holdedWrite.CreateContactAsync(new HoldedContactInput
            {
                Name = legalName,
                TradeName = tradeName,
                Type = "creditor",
                Iban = string.IsNullOrWhiteSpace(iban) ? null : iban,
            }, ct);
            minted = true;
        }

        // A refused seed cannot collide, so anything left is a pre-existing overlap on the member's own binding: not
        // this push's doing, but reported.
        var accountNum = binding?.SupplierAccountNum ?? seedAccountNum;
        var conflict = FindConflictingBinding(allBindings, userId, accountNum, contactId);
        if (conflict is not null)
            LogBindingCollision(
                nameof(EnsureCreditorContactAsync), userId, contactId, accountNum, conflict);

        // A member already holding this contact has nothing to write but UpdatedAt, and the push is several Holded
        // calls long, so writing this copy would clobber an admin's change meanwhile. Skipping is what makes Unbind
        // hold against an in-flight push.
        if (binding is not null
            && string.Equals(binding.HoldedContactId, contactId, StringComparison.Ordinal)
            && accountNum == binding.SupplierAccountNum)
            return contactId;

        // A binding missing its number writes real content, so it cannot skip; re-reading here shrinks the window in
        // which it undoes an admin's Bind or Unbind. A contact this call just minted may have lost to a concurrent
        // drain's own, so that case checks and retracts ours if it lost.
        if (minted && binding is null)
        {
            var bound = await repo.GetCreditorContactByUserAsync(userId, ct);
            if (bound is { HoldedContactId.Length: > 0 })
                return await AdoptBoundContactAsync(userId, contactId, minted, bound, jobName, ct);
        }
        else if (!await BindingUnchangedAsync(nameof(EnsureCreditorContactAsync), userId, binding, ct))
            return contactId;

        var now = clock.GetCurrentInstant();
        var row = new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),                                   // ignored on update (keyed by UserId)
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = accountNum,
            Source = binding?.Source ?? CreditorContactSource.Auto, // preserve a Manual binding
            CreatedAt = now,
            UpdatedAt = now,
        };
        // A minted contact's binding is insert-only: an upsert could take its update branch over a
        // winner's binding that landed after the re-read above, and orphan the winner's contact.
        var written = minted && binding is null
            ? await repo.TryInsertCreditorContactAsync(row, ct)
            : await repo.UpsertCreditorContactAsync(row, now, ct);
        if (written) return contactId;

        // A concurrent run took the member's one binding slot between the read above and this write.
        var winner = await repo.GetCreditorContactByUserAsync(userId, ct)
            ?? throw new InvalidOperationException(
                $"Creditor binding insert for member {userId} hit the unique index but no binding exists.");
        return await AdoptBoundContactAsync(userId, contactId, minted, winner, jobName, ct);
    }

    /// <summary>A concurrent run bound <paramref name="bound"/> first, so that is the member's contact and one this
    /// call minted is deleted from Holded, audited. A failed delete is logged, not thrown; the orphan then shows on
    /// /Finance/HoldedDuplicates.</summary>
    private async Task<string> AdoptBoundContactAsync(
        Guid userId, string contactId, bool minted, HoldedCreditorContact bound, string jobName,
        CancellationToken ct)
    {
        if (!minted || string.Equals(bound.HoldedContactId, contactId, StringComparison.Ordinal))
            return bound.HoldedContactId;

        try
        {
            await holdedWrite.DeleteContactAsync(contactId, ct);
            await audit.LogAsync(FinanceAuditActions.HoldedDuplicateRemoved, AuditSubjectTypes.User, userId,
                $"DELETED duplicate Holded contact {contactId} that a concurrent push created; kept "
                + $"{bound.HoldedContactId}.",
                jobName);
            logger.LogWarning(
                "Member {UserId} was bound to Holded contact {BoundContactId} by a concurrent push; the " +
                "contact {MintedContactId} this push created was deleted from Holded.",
                userId, bound.HoldedContactId, contactId);
        }
        catch (HoldedApiException ex)
        {
            logger.LogError(ex,
                "Member {UserId} was bound to Holded contact {BoundContactId} by a concurrent push, but " +
                "deleting the duplicate contact {MintedContactId} this push created failed — delete it " +
                "in Holded by hand.",
                userId, bound.HoldedContactId, contactId);
        }
        return bound.HoldedContactId;
    }

    internal async Task SetCreditorAccountNumAsync(
        Guid userId, int supplierAccountNum, string jobName, CancellationToken ct = default)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        if (binding is null) return;

        // Holded's own word, so it is written either way; the contact-id overlap is covered upstream.
        var conflict = FindConflictingBinding(
            await repo.GetCreditorContactsAsync(ct), userId, supplierAccountNum, holdedContactId: null);
        if (conflict is not null)
            LogBindingCollision(
                nameof(SetCreditorAccountNumAsync), userId, binding.HoldedContactId,
                supplierAccountNum, conflict);

        // Same re-check as EnsureCreditorContactAsync: this row carries values read above, so writing it over an
        // admin's newer binding would revert it.
        if (!await BindingUnchangedAsync(nameof(SetCreditorAccountNumAsync), userId, binding, ct)) return;

        var now = clock.GetCurrentInstant();
        var written = await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = binding.Id,
            UserId = userId,
            HoldedContactId = binding.HoldedContactId,
            SupplierAccountNum = supplierAccountNum,
            Source = binding.Source,
            CreatedAt = binding.CreatedAt,
            UpdatedAt = now,
        }, now, ct);

        // Which account a member is paid from is the Board's business, and this write moves it on Holded's word alone,
        // so every change is audited, a first resolution included.
        if (!written || binding.SupplierAccountNum == supplierAccountNum) return;
        await EnsureContactMirroredAsync(binding.HoldedContactId, supplierAccountNum, ct);
        var previous = binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)";
        await audit.LogAsync(FinanceAuditActions.HoldedCreditorAccountNumChanged, HoldedCreditorAccount, userId,
            $"Creditor account changed from {previous} to {supplierAccountNum} (Holded contact "
            + $"{binding.HoldedContactId}), as Holded reported after the push",
            jobName, userId, AuditSubjectTypes.User);
    }

    internal async Task<bool> ClearCreditorContactAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        // The audit names the row the delete removed, not an earlier read a push could have overtaken.
        if (await DeleteCreditorBindingAsync(userId, ct) is not { } binding) return false;

        await audit.LogAsync(FinanceAuditActions.HoldedCreditorUnbound, HoldedCreditorAccount, userId,
            $"Unbound creditor account {binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)"} "
            + $"(Holded contact {binding.HoldedContactId})",
            actorUserId, userId, AuditSubjectTypes.User);
        return true;
    }

    internal async Task<HoldedCreditorContact?> DeleteCreditorBindingAsync(Guid userId, CancellationToken ct)
    {
        var removed = await repo.DeleteCreditorContactAsync(userId, ct);
        if (removed is not null)
            logger.LogInformation("Cleared the creditor binding for member {UserId}.", userId);
        return removed;
    }
}
