using Humans.AuditLog.Contracts;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Globalization;
using System.Text.RegularExpressions;
using static Humans.Finance.Services.SepaText;

namespace Humans.Finance.Services;

/// <summary>Owns bank-line matching, SEPA payment booking and reconciliation for manual actions and the sweep.</summary>
internal sealed class SepaBankBooking(
    IHoldedRepository repo,
    IHoldedClient client,
    IClock clock,
    TrackedCache<string, Dictionary<string, int>> bookingState,
    IAuditLogService audit,
    IOptions<SepaOptions> sepa,
    ILogger logger) : ISepaBankBooking, IApplicationService
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    // Persisted audit entity type, shared with payout generation.
    private const string SepaTransferEntityType = nameof(SepaPayoutTransfer);

    // ─── SEPA booking against the bank line (nobodies-collective/Humans#1185) ───

    /// <summary>The job name the sweep's audit entries carry in place of a human actor.</summary>
    private const string SepaBookingJobName = "sepa-bank-booking";

    /// <summary>The audit entity type of a Holded bank line the sweep skipped; the line has no Guid,
    /// so its movement id rides in the description.</summary>
    private const string SepaBankLineEntityType = "SepaBankLine";

    /// <summary>Below half a cent is zero at two decimal places.</summary>
    private const decimal CentEpsilon = 0.005m;

    /// <summary>How many payment ids a reconcile call falls back to per request once Holded has
    /// refused the whole list. A member with 100+ open documents is paid in one transfer, so one
    /// bank line can name that many payments, and nothing about the reconcile endpoint has been
    /// exercised above a handful — the one-call form goes first, and this is the fallback.</summary>
    private const int ReconcileChunkSize = 50;

    /// <summary>Serialises every SEPA booking on this server. The row's <c>BookedAt</c> is read
    /// before the Holded postings and written after them, so without this two concurrent bookings of
    /// one transfer both read "not booked" and both post the whole amount.</summary>
    private static readonly SemaphoreSlim BookingGate = new(1, 1);

    /// <summary>How many consecutive sweep runs may fail closed on the same pending outgoing line with
    /// no readable date before the sweep skips that line and books the rest (peterdrier/Humans#1946).
    /// The sweep runs every two hours, so the feed stays blocked about six hours before a stuck line
    /// stops costing finance automation its whole feed.</summary>
    private const int StuckLineSkipAfterRuns = 3;

    /// <summary>Consecutive sweep runs each stuck line has failed, by Holded movement id, kept in
    /// the singleton tracked cache under <see cref="CacheKeys.SepaStuckBankLines"/> because the
    /// service is scoped and the count must outlive one run. In memory because the sweep is one
    /// Hangfire job on one server — a restart only restarts the count, which keeps failing closed a
    /// little longer, the safe direction. A line absent from a run drops out, so a line that resolves,
    /// or a different stuck line, starts from one.</summary>
    /// <summary>The slack on either end of the tagged-lines window. The window itself spans the
    /// file's generation day to today — a posting this flow made is dated the bank line, but one a
    /// pre-#1185 run made is dated the click.</summary>
    private const int TagWindowDays = 7;

    /// <summary>How far back a transfer's file may have been generated and still be matched. The feed
    /// itself is read <see cref="GenerationSlackDays"/> further back (<see cref="FeedFrom"/>), because a
    /// line may pay a file dated up to that many days after it.</summary>
    private const int FeedWindowDays = 90;

    private const string ReconciledStatus = "reconciled";

    private const string PendingStatus = "pending";

    /// <summary>How far before its file's generation day a bank line may be dated and still be
    /// paying it — the bank can value-date a day early, and the feed's dates are Madrid days.</summary>
    private const int GenerationSlackDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays;

    /// <summary>The remittance text the payout file writes — <c>&lt;account&gt; - NCA - &lt;name&gt;</c>.
    /// Matched anywhere in the line, not anchored: bank feeds prepend their own wording to the
    /// Ustrd.</summary>
    private static readonly Regex RemittanceAccount = new(
        @"(?<acct>\d{8})\s*-\s*NCA\s*-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>The same rows and reason <see cref="GetSepaPayoutsAsync"/> computes — Backdoor's
    /// read-only <c>sepa-transfers</c> route reuses the booking-state logic short of the live
    /// bank-feed read, which exists only to offer <c>/Finance/Sepa</c>'s own "book this" button and
    /// whose candidate-line/unmatched-movements halves Backdoor never projects
    /// (peterdrier/Humans#1838).</summary>
    public async Task<(IReadOnlyList<SepaPayoutTransferRow> Transfers, string? UnavailableReason)>
        GetSepaTransfersAsync(CancellationToken ct = default) => await GetSepaPayoutRowsWithReasonsAsync(ct);

    public async Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason,
        IReadOnlyList<SepaBankMovementVm> UnmatchedMovements, string? BankFeedError)>
        GetSepaPayoutsAsync(int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays,
            CancellationToken ct = default)
    {
        var tolerance = ClampTolerance(toleranceDays);
        var (withReasons, unavailable) = await GetSepaPayoutRowsWithReasonsAsync(ct);
        if (withReasons.Count == 0 || unavailable is not null) return (withReasons, unavailable, [], null);
        // A withdrawn transfer renders on its row and nowhere else: no line can be its payment.
        var live = Live(withReasons);

        IReadOnlyList<HoldedBankMovementDto> movements;
        try
        {
            movements = await ReadBankFeedAsync(FeedFrom(tolerance), ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            // The page still lists everything it lists; it just cannot offer a line to book against.
            logger.LogWarning(ex, "The Sabadell bank feed could not be read for /Finance/Sepa.");
            // Admin-only page: the Holded message names the line or the call that failed, which is
            // what the treasurer needs to fix it.
            return (withReasons, null, [],
                "The Sabadell bank feed could not be read, so no transfer can be booked right now: "
                + ex.Message);
        }

        var unmatched = new List<SepaBankMovementVm>();
        var candidateByTransfer = new Dictionary<Guid, (HoldedBankMovementDto Line, bool ByText)>();
        var considered = movements
            .Where(NeedsAMatch)
            // A line this section already booked is nobody's problem — it renders on its own row.
            .Where(m => !withReasons.Any(
                r => string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            .Select(m =>
            {
                var account = RemittanceAccountNum(m.Description);
                // Ambiguity is judged over every unbooked row, bookable or not — the same set
                // PairingRefusal uses at booking time, so a line the page pairs is one the booking
                // accepts. A not-bookable row can still be the line's only match; it then goes to
                // the panel rather than claiming the row. A textless line (Sabadell drops the
                // remittance text) matches on amount and date alone — and never while it could also
                // be a whole file's debit.
                return (Movement: m, Account: account, Matches:
                    account is null && TextlessLineFitsAFile(live, m, tolerance)
                        ? []
                        : UnbookedMatches(live, account, Math.Abs(m.Amount), m.Date, tolerance));
            })
            .ToList();

        // A transfer two bank lines could each have paid is as ambiguous as a line two transfers
        // could each have asked for: Humans cannot tell which line moved the money, so neither one
        // books it and both go to the panel. Every line that could be the transfer's counts, an
        // ambiguous textless one included — PairingRefusal's rival check counts the same lines.
        var claimants = considered
            .SelectMany(c => c.Matches.Select(r => r.TransferId))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        // A line the bank debited for a whole file at once. Read over every outgoing line, claimed or
        // not: a batch a Process run only got part-way through already carries this line on its
        // booked rows, and has to stay processable to finish.
        var batchLines = movements
            .Where(NeedsAMatch)
            .Select(m => (Movement: m, Files: BatchFiles(live, m, movements, tolerance)))
            .ToList();
        // Every line a file fits counts against it, ambiguous or not — as Process's rival check does.
        var batchClaimants = batchLines
            .SelectMany(b => b.Files)
            .GroupBy(f => f)
            .ToDictionary(g => g.Key, g => g.Count());
        var batchByFile = batchLines
            .Where(b => b.Files.Count == 1 && batchClaimants[b.Files[0]] == 1)
            .ToDictionary(b => b.Files[0], b => b.Movement);
        var batchFilesByLine = batchLines
            .GroupBy(b => b.Movement.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Files, StringComparer.Ordinal);

        foreach (var (m, account, matches) in considered)
        {
            if (matches.Count == 1 && claimants[matches[0].TransferId] == 1
                && matches[0].NotBookableReason is null)
            {
                candidateByTransfer[matches[0].TransferId] = (m, account is not null);
                continue;
            }

            // Equal amounts in one file, as many textless lines: the set pairs one to one. A member
            // that cannot be booked keeps the whole set on the panel, so Book file never books
            // half of it.
            if (account is null && matches.Count > 1
                && TextlessSetPairs(live, movements, m, tolerance) is { Count: > 0 } set
                && set.All(p => p.Row.NotBookableReason is null))
            {
                candidateByTransfer[set.Single(p => string.Equals(p.Line.Id, m.Id, StringComparison.Ordinal)).Row.TransferId]
                    = (m, false);
                continue;
            }

            var files = batchFilesByLine.GetValueOrDefault(m.Id) ?? [];
            // It renders on its file's card, with the Process button.
            if (files.Count == 1 && batchByFile.TryGetValue(files[0], out var batch)
                && string.Equals(batch.Id, m.Id, StringComparison.Ordinal))
                continue;

            unmatched.Add(new SepaBankMovementVm(
                m.Id, m.Date, m.Amount, m.Description, account, m.Status,
                files.Count > 1
                    ? $"{files.Count} payout files total {Euros(Math.Abs(m.Amount))} — settle it in Holded by hand"
                    : files.Count == 1
                        ? "another bank line matches that file's total too — settle it in Holded by hand"
                        : account is null && TextlessLineFitsAFile(live, m, tolerance)
                            ? "its text names no creditor account, and it could be one transfer's line or "
                              + "a whole file's — settle it in Holded by hand"
                            : matches.Count == 0
                                ? account is null
                                    ? $"its text names no creditor account, and no unbooked transfer is for {Euros(Math.Abs(m.Amount))}"
                                    : $"no unbooked transfer of {Euros(Math.Abs(m.Amount))} on account {account}"
                                : matches.Count > 1
                                    ? account is null
                                        ? $"{matches.Count} unbooked transfers are for {Euros(Math.Abs(m.Amount))} and its text names no creditor account — book it by hand"
                                        : $"{matches.Count} unbooked transfers match it — settle it in Holded by hand"
                                    : matches[0].NotBookableReason is { } why
                                        ? $"the one transfer it matches (account {matches[0].SupplierAccountNum}) cannot be booked: {why}"
                                        : "another bank line matches that transfer too — settle it in Holded by hand"));
        }

        var batchVmByFile = batchByFile.ToDictionary(kv => kv.Key, kv => BatchLineVm(kv.Key, kv.Value));

        return (
            withReasons
                .Select(r => candidateByTransfer.TryGetValue(r.TransferId, out var c)
                    ? r with
                    {
                        CandidateBankMovementId = c.Line.Id,
                        CandidateBankMovementDate = c.Line.Date,
                        CandidateBankMovementAmount = c.Line.Amount,
                        CandidateBankMovementDescription = c.Line.Description,
                        CandidateMatchedByText = c.ByText,
                    }
                    : r)
                .Select(r => batchVmByFile.TryGetValue(r.FileId, out var batch) ? r with { BatchLine = batch } : r)
                .ToList(),
            null, unmatched, null);

        SepaBatchLineVm BatchLineVm(Guid fileId, HoldedBankMovementDto m) => new(
            m.Id, m.Date, m.Amount, m.Description,
            QuotesFileReference: (m.Description ?? "").Contains(
                fileId.ToString("N"), StringComparison.OrdinalIgnoreCase),
            NotProcessableReason: live
                .Where(r => r.FileId == fileId && !r.IsBooked && r.NotBookableReason is not null)
                .Select(r => $"the transfer on {r.SupplierAccountNum}: {r.NotBookableReason}")
                .FirstOrDefault());
    }

    /// <summary>The pre-feed half of <see cref="GetSepaPayoutsAsync"/>: every transfer row with its
    /// <see cref="SepaPayoutTransferRow.NotBookableReason"/> filled in, no live Holded read. Its own
    /// method so <see cref="GetSepaTransfersAsync"/> (Backdoor) can reuse it without the bank-feed
    /// call, which exists only to offer <c>/Finance/Sepa</c>'s "book this" button
    /// (peterdrier/Humans#1838).</summary>
    private async Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason)>
        GetSepaPayoutRowsWithReasonsAsync(CancellationToken ct)
    {
        var rows = await repo.GetSepaPayoutTransferRowsAsync(ct);
        var unavailable = BookingUnavailableReason();
        if (rows.Count == 0 || unavailable is not null) return (rows, unavailable);

        // UserId is the one column the DB keeps unique, so this cannot throw.
        var bindingByUser = (await repo.GetCreditorContactsAsync(ct)).ToDictionary(c => c.UserId);
        var withReasons = rows
            .Select(r => r with { NotBookableReason = NotBookableReason(r, bindingByUser) })
            .ToList();
        return (withReasons, null);
    }

    /// <summary>Why this transfer cannot be booked into Holded, or null when it can — the row's reason
    /// on <c>/Finance/Sepa</c>, and the gate a batch Process checks for every transfer before it
    /// posts anything.</summary>
    private string? NotBookableReason(
        SepaPayoutTransferRow row, IReadOnlyDictionary<Guid, HoldedCreditorContact> bindingByUser)
    {
        if (row.IsBooked) return null;   // the row renders as booked; no reason to show
        if (row.IsWithdrawn) return null;   // renders as withdrawn; nothing books it
        // Payments landed and the run stopped before booking. Holded keeps no tag on a purchase
        // payment, so a retry cannot see them and would post the amount again as a journal entry.
        if (row.PostedButNotBooked)
            return "an earlier attempt posted Holded payment(s) " + row.HoldedPaymentRefs
                   + " and then stopped — Humans cannot see them again; settle it in Holded by hand";
        // Nothing in Humans can still book this one: the line that paid it, if any, has dropped
        // off the feed. Say so rather than leaving it in "waiting for the Sabadell line" forever.
        if (IsStale(row))
            return $"generated more than {FeedWindowDays} days ago — no bank line on the feed can "
                   + "book it now; settle it in Holded by hand";
        if (!bindingByUser.TryGetValue(row.UserId, out var binding)
            || string.IsNullOrEmpty(binding.HoldedContactId))
            return "the member has no Holded contact binding";
        if (binding.SupplierAccountNum != row.SupplierAccountNum)
            return "the member's Holded binding changed since this file was generated — book it by hand";
        // Mirrors BookSepaTransferAsync's sibling-contact refusal: same account number is not
        // enough, because Holded lets two contacts share one 400000xx. Without this the button
        // renders live and only fails on click. Null means a row generated before
        // nobodies-collective/Humans#1146 shipped — account-only behaviour, as there.
        if (row.HoldedContactId is { Length: > 0 }
            && !string.Equals(row.HoldedContactId, binding.HoldedContactId, StringComparison.Ordinal))
            return "the member was rebound to a different Holded contact since this file was generated — book it by hand";
        return null;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (BookingUnavailableReason() is not null || !client.IsConfigured) return;

        var rows = Live(await repo.GetSepaPayoutTransferRowsAsync(ct));
        var pending = rows.Where(r => r.ReconcilePending).ToList();
        var unbooked = rows.Where(r => r.BookedAt is null).ToList();
        if (pending.Count == 0 && unbooked.Count == 0) return;

        // The invariant: every reconcile-pending row's bank line lies inside the window read for it.
        // A pending row's line is already known and already booked — it only has to be looked at
        // again — so its lower bound is its own first payable day with no floor; a row that ages out
        // of the feed window would otherwise never see a human's later reconcile and would keep
        // ReconciledAt and its audit entry missing forever. The feed floor is about matching *new*
        // transfers, so it applies to that bound alone. Widening the read cannot widen matching:
        // UnbookedMatches takes no stale row (GeneratedAt inside the window) and no line dated before
        // the file's first payable day, so every line it can act on is inside the floor anyway. Both bounds key off
        // GeneratedAt, never BookedAt: a line may only pay a file generated on or before it
        // (FirstPayableDate), while a booking can land days later, so a BookedAt window would start
        // *after* the very line a pending row is waiting to see reconciled.
        var floor = FeedFrom(GenerationSlackDays);
        var matchFrom = unbooked.Select(FirstPayableDate).DefaultIfEmpty(floor).Min();
        if (matchFrom < floor) matchFrom = floor;
        var pendingFrom = pending.Select(FirstPayableDate).DefaultIfEmpty(matchFrom).Min();
        var from = pendingFrom < matchFrom ? pendingFrom : matchFrom;

        IReadOnlyList<HoldedBankMovementDto> movements;
        try
        {
            movements = await ReadSweepFeedAsync(from, ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "The SEPA sweep could not read the Sabadell bank feed; nothing was booked.");
            return;
        }

        var byId = movements.ToLookup(m => m.Id, StringComparer.Ordinal);

        // A pending row is re-reconciled from the payment ids recorded on it — a booking made while
        // the token could not write to the bank feed never got its reconcile through, and only a
        // re-issue clears it. Rows of one Processed file share a line and are re-issued together, as
        // they were booked. A line Holded already reads reconciled (a later sweep's booking, or a
        // human in the GUI) is stamped as is; one that is `partial` is not re-issued: its payments
        // are linked and what is left (a journal remainder, a hand reconcile) is a human's. A file
        // a Process run got part-way through is not re-issued either: linking the booked part would
        // turn the line `partial`, and the retry that finishes the file refuses a touched line.
        foreach (var group in pending.GroupBy(r => r.HoldedBankMovementId!, StringComparer.Ordinal))
        {
            var line = byId[group.Key].FirstOrDefault();
            if (line is null) continue;
            Instant? at = IsReconciled(line) ? clock.GetCurrentInstant() : null;
            var fileStillOpen = rows.Any(r => !r.IsBooked && group.Any(g => g.FileId == r.FileId));
            if (at is null && IsUntouched(line) && !fileStillOpen)
            {
                var refs = group.SelectMany(r => PaymentRefs(r.HoldedPaymentRefs)).ToList();
                if (refs.Count > 0)
                {
                    logger.LogInformation(
                        "SEPA sweep: re-issuing the reconcile of Sabadell line {MovementId} against "
                        + "{Count} payment(s) of {Rows} transfer(s).", line.Id, refs.Count, group.Count());
                    at = await TryReconcileAsync(line.Id, line.Date, refs, incomplete: true, ct);
                }
            }
            if (at is null) continue;

            foreach (var row in group)
            {
                await repo.MarkSepaTransferReconciledAsync(row.TransferId, at.Value, ct);
                await audit.LogAsync(
                    AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, row.TransferId,
                    $"RECONCILED the Sabadell line {row.HoldedBankMovementId} that booked the SEPA payout "
                    + $"{Euros(row.Amount)} to {row.IbanMasked} on creditor account {row.SupplierAccountNum}.",
                    SepaBookingJobName, row.UserId, nameof(User));
            }
        }

        foreach (var m in movements.Where(NeedsAMatch))
        {
            if (rows.Any(r => string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
                continue;

            // A textless line is matched on amount and date by /Finance/Sepa, where a human approves
            // it; the sweep books only a line whose text names the account.
            var account = RemittanceAccountNum(m.Description);
            if (account is null) continue;

            var matches = UnbookedMatches(unbooked, account.Value, Math.Abs(m.Amount), m.Date, GenerationSlackDays);
            if (matches.Count != 1)
            {
                // The page's "bank lines needing a human" panel is where this is surfaced; the sweep
                // only says so once per run in the log.
                logger.LogInformation(
                    "SEPA sweep: Sabadell line {MovementId} matches {Count} unbooked transfer(s) on "
                    + "account {Account} — left for a human.", m.Id, matches.Count, account.Value);
                continue;
            }

            try
            {
                var result = await BookSepaTransferAsync(matches[0].TransferId, m.Id, actorUserId: null);
                if (!result.Succeeded)
                    logger.LogWarning(
                        "SEPA sweep: transfer {TransferId} was not booked against line {MovementId}: {Reason}",
                        matches[0].TransferId, m.Id, result.Message);
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                // One bad line never aborts the sweep.
                logger.LogError(ex,
                    "SEPA sweep: booking transfer {TransferId} against line {MovementId} threw.",
                    matches[0].TransferId, m.Id);
            }
        }
    }

    /// <summary>The sweep's feed read. A pending outgoing line with no readable date fails the read
    /// closed — it could be a SEPA rival (peterdrier/Humans#1861) — so the first
    /// <see cref="StuckLineSkipAfterRuns"/> − 1 runs on one such line book nothing. On the run that
    /// reaches the threshold the line is skipped, the rest of the feed is read, and one audit entry
    /// says so; later runs skip it with a warning each time until it reads again.</summary>
    private async Task<IReadOnlyList<HoldedBankMovementDto>> ReadSweepFeedAsync(
        LocalDate from, CancellationToken ct)
    {
        var previous = bookingState.TryGet(CacheKeys.SepaStuckBankLines, out var runsBefore) ? runsBefore : [];
        var thisRun = new Dictionary<string, int>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            try
            {
                var movements = await ReadBankFeedAsync(from, ct, skip);
                RememberStuckLines(thisRun);
                return movements;
            }
            catch (HoldedPermanentException ex) when (ex.BankMovementId is { } id && !skip.Contains(id))
            {
                var runs = previous.GetValueOrDefault(id) + 1;
                thisRun[id] = runs;
                if (runs < StuckLineSkipAfterRuns)
                {
                    RememberStuckLines(thisRun);
                    throw;
                }

                skip.Add(id);
                RememberStuckLines(thisRun);
                logger.LogWarning(
                    "SEPA sweep: skipping Sabadell line {MovementId}, pending outgoing with no readable date "
                    + "for {Runs} consecutive runs; the rest of the feed is booked without it.", id, runs);
                if (runs == StuckLineSkipAfterRuns)
                    await audit.LogAsync(
                        AuditAction.SepaBankLineSkipped, SepaBankLineEntityType, Guid.Empty,
                        $"The SEPA sweep skipped Sabadell line {id}: it has been pending outgoing with no "
                        + $"readable date for {runs} consecutive runs, and blocked every booking until now. "
                        + "The rest of the feed books without it, so it can no longer count as a rival of a "
                        + "transfer's amount — fix or reconcile it in Holded.",
                        SepaBookingJobName);
            }
        }
    }

    private void RememberStuckLines(Dictionary<string, int> runs)
    {
        if (runs.Count == 0) bookingState.Invalidate(CacheKeys.SepaStuckBankLines);
        else bookingState.Set(CacheKeys.SepaStuckBankLines, runs);
    }

    public Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId,
        int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        RunBookingAsync(() => BookOneTransferAsync(
            transferId, bankMovementId, actorUserId, ClampTolerance(toleranceDays)));

    public Task<SepaBookingResult> BookSepaFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId,
        int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        RunBookingAsync(() => BookOneFileAsync(fileId, bankMovementId, actorUserId, ClampTolerance(toleranceDays)));

    public Task<SepaBookingResult> BookSepaFileMatchesAsync(
        Guid fileId, Guid actorUserId,
        int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        RunBookingAsync(() => BookFileMatchesAsync(fileId, actorUserId, ClampTolerance(toleranceDays)));

    /// <summary>A tolerance is days either side of the file date, never negative and never past
    /// the feed window — beyond it there is no line to match anyway.</summary>
    private static int ClampTolerance(int days) => Math.Clamp(days, 0, FeedWindowDays);

    /// <summary>The page's pairing, re-run now, and the all-or-nothing gate: a file whose rows are
    /// not all matched is not approved at all, so a treasurer never books half a file and hunts the
    /// other half by hand. Each row then goes through <see cref="BookOneTransferAsync"/>, which
    /// re-validates its own line; the first refusal stops the run, booked rows stay booked.</summary>
    private async Task<SepaBookingResult> BookFileMatchesAsync(Guid fileId, Guid actorUserId, int tolerance)
    {
        var (rows, unavailable, _, feedError) = await GetSepaPayoutsAsync(tolerance, CancellationToken.None);
        if (unavailable is not null) return new SepaBookingResult(false, unavailable);
        if (feedError is not null) return new SepaBookingResult(false, feedError + " Nothing was posted.");

        var file = rows.Where(r => r.FileId == fileId).ToList();
        if (file.Count == 0)
            return new SepaBookingResult(false, "That payout file no longer exists — reload the page.");
        // A withdrawn transfer is not the file's to book: the gate is over the rest.
        var unbooked = Live(file).Where(r => !r.IsBooked).OrderBy(r => r.SupplierAccountNum).ToList();
        if (unbooked.Count == 0)
            return new SepaBookingResult(false, "Every transfer in that file is already booked or withdrawn — nothing was posted.");
        var unmatched = unbooked.Where(r => !r.CanBook).ToList();
        if (unmatched.Count > 0)
            return new SepaBookingResult(false,
                $"{unmatched.Count} of the file's {unbooked.Count} unbooked transfers have no Sabadell line "
                + $"(creditor account{(unmatched.Count == 1 ? "" : "s")} "
                + string.Join(", ", unmatched.Select(r => r.SupplierAccountNum.ToString(CultureInfo.InvariantCulture)))
                + ") — the file is approved only when every transfer in it is matched. Nothing was posted.");

        var booked = 0;
        foreach (var row in unbooked)
        {
            var result = await BookOneTransferAsync(row.TransferId, row.CandidateBankMovementId!, actorUserId, tolerance);
            if (!result.Succeeded)
                return new SepaBookingResult(false,
                    $"{booked} of {unbooked.Count} transfers booked; the one on creditor account "
                    + $"{row.SupplierAccountNum} was not: {result.Message} Approve the file again to "
                    + "book the rest.");
            booked++;
        }

        return new SepaBookingResult(true,
            $"Booked all {booked} transfer{(booked == 1 ? "" : "s")} of {file[0].FileName} against "
            + "their Sabadell lines.");
    }

    private async Task<SepaBookingResult> RunBookingAsync(Func<Task<SepaBookingResult>> booking)
    {
        if (BookingUnavailableReason() is { } unavailable)
            return new SepaBookingResult(false, unavailable);

        // The "is it already booked?" read and the stamp that answers it sit either side of several
        // Holded round-trips, so two callers inside that window — a Book click while the sweep runs,
        // or a double-submitted form — would both read "not booked" and both post the whole amount.
        // One server, and a booking takes seconds: serialise both entry points outright.
        await BookingGate.WaitAsync(CancellationToken.None);
        try
        {
            return await booking();
        }
        finally
        {
            BookingGate.Release();
        }
    }

    /// <summary>What the transfers of one batch hand back to the file that owns their shared bank
    /// line: the postings to reconcile it against, once, after the last of them.</summary>
    private sealed class BatchBooking(HoldedBankMovementDto movement, int transferCount)
    {
        public HoldedBankMovementDto Movement { get; } = movement;

        public int TransferCount { get; } = transferCount;

        public List<HoldedReconcileDocumentRef> Docs { get; } = [];

        /// <summary>Some of the line's money cannot be named to Holded — a resumed transfer, an
        /// unconfirmed payment, a journal entry — so a reconcile Holded accepts may still leave it
        /// <c>partial</c>.</summary>
        public bool Incomplete { get; set; }
    }

    private async Task<SepaBookingResult> BookOneFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId, int tolerance)
    {
        // Same rule as a single booking: once one transfer is posted, the rest have to follow.
        var ct = CancellationToken.None;

        // Withdrawn transfers left the file before the bank debited it: the total the line has to
        // match is the rest.
        var rows = Live(await repo.GetSepaPayoutTransferRowsAsync(ct));
        var file = rows.Where(r => r.FileId == fileId).ToList();
        if (file.Count == 0)
            return new SepaBookingResult(false, "That payout file no longer exists — reload the page.");

        IReadOnlyList<HoldedBankMovementDto> feed;
        try
        {
            feed = await ReadBankFeedAsync(FeedFrom(tolerance), ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Could not read the Sabadell bank feed to process SEPA file {FileId}.", fileId);
            return new SepaBookingResult(false, "The Sabadell bank feed could not be read — nothing was posted.");
        }

        var movement = feed.FirstOrDefault(x => string.Equals(x.Id, bankMovementId, StringComparison.Ordinal));
        if (movement is null)
            return new SepaBookingResult(false,
                $"That Sabadell line is no longer in the last {FeedWindowDays} days of the feed — "
                + "reload /Finance/Sepa.");

        // ── The pairing, re-validated here — the posted ids are never trusted.
        if (BatchLineRefusal(file, movement, rows, feed, tolerance) is { } refusal)
            return new SepaBookingResult(false, refusal);
        var files = BatchFiles(rows, movement, feed, tolerance);
        if (files.Count > 1)
            return new SepaBookingResult(false,
                $"{files.Count} payout files total {Euros(Math.Abs(movement.Amount))} — Humans cannot tell "
                + "which one that Sabadell line paid; settle it in Holded by hand. Nothing was posted.");
        var rivals = feed.Count(x => !string.Equals(x.Id, movement.Id, StringComparison.Ordinal)
                                     && NeedsAMatch(x) && BatchLineRefusal(file, x, rows, feed, tolerance) is null);
        if (rivals > 0)
            return new SepaBookingResult(false,
                $"{rivals + 1} Sabadell lines could each be this file's debit — Humans cannot tell which "
                + "one is; settle it in Holded by hand. Nothing was posted.");

        // Every transfer is checked before any is posted, so a bad binding refuses the whole file
        // rather than stopping it half-way.
        var bindingByUser = (await repo.GetCreditorContactsAsync(ct)).ToDictionary(c => c.UserId);
        var unbooked = file.Where(r => !r.IsBooked).OrderBy(r => r.SupplierAccountNum).ToList();
        foreach (var row in unbooked)
            if (NotBookableReason(row, bindingByUser) is { } why)
                return new SepaBookingResult(false,
                    $"The transfer on {row.SupplierAccountNum} cannot be booked: {why}. Nothing was posted.");

        var batch = new BatchBooking(movement, file.Count);
        var booked = 0;
        foreach (var row in unbooked)
        {
            var result = await BookOneTransferAsync(row.TransferId, movement.Id, actorUserId, tolerance, batch);
            if (!result.Succeeded)
                return new SepaBookingResult(false,
                    $"{booked} of {unbooked.Count} transfer(s) booked, then the one on "
                    + $"{row.SupplierAccountNum} was not: {result.Message} Process the file again to "
                    + "finish — booked transfers are skipped.");
            booked++;
        }

        // ── One reconcile for the whole line, against every posting this run made. A file an
        // earlier run got part-way through cannot name that run's postings, so only Holded saying
        // "reconciled" afterwards stamps it — the sweep keeps re-checking otherwise.
        var resumed = unbooked.Count < file.Count;
        var reconciledAt = await TryReconcileAsync(
            movement.Id, movement.Date, batch.Docs, batch.Incomplete || resumed, ct);
        if (reconciledAt is not null)
            foreach (var row in file)
            {
                await repo.MarkSepaTransferReconciledAsync(row.TransferId, reconciledAt.Value, ct);
                await audit.LogAsync(
                    AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, row.TransferId,
                    $"RECONCILED the Sabadell line {movement.Id} that paid the whole file "
                    + $"{row.FileName}, including this transfer of {Euros(row.Amount)} to "
                    + $"{row.IbanMasked} on creditor account {row.SupplierAccountNum}.",
                    actorUserId, row.UserId, nameof(User));
            }

        return new SepaBookingResult(true,
            $"Booked {booked} transfer(s) of {file[0].FileName} against Sabadell line {movement.Id} "
            + $"({Euros(movement.Amount)}); "
            + (reconciledAt is not null
                ? "the line is reconciled."
                : "RECONCILE PENDING — reconcile the line in Holded by hand; the sweep re-issues it meanwhile."));
    }

    private async Task<SepaBookingResult> BookOneTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId, int tolerance, BatchBooking? batch = null)
    {
        // No request-scoped token reaches this method at all — once a payment is posted to Holded the
        // rest of the allocation has to finish (memory/architecture/cancellation-token-propagation.md).
        var ct = CancellationToken.None;

        var transfer = await repo.GetSepaTransferAsync(transferId, ct);
        if (transfer is null)
            return new SepaBookingResult(false, "That transfer no longer exists — reload the page.");

        // Idempotency is the row itself, not a UI state: a second POST of the same form finds
        // BookedAt set and pays nothing.
        if (transfer.BookedAt is not null)
            return new SepaBookingResult(false,
                $"Transfer to {transfer.IbanMasked} is already booked — nothing was posted to Holded.");

        // Cancelled at the bank: no line is this transfer's, whatever the amount says.
        if (transfer.WithdrawnAt is not null)
            return new SepaBookingResult(false,
                $"Transfer to {transfer.IbanMasked} was withdrawn — nothing was posted to Holded.");

        // An earlier run posted payments and then stopped (PARTIAL). Holded discards the
        // description on a purchase payment, so the step-4 tag sum cannot see them, and a retry
        // would post the whole amount again as a journal entry. Refuse until a human has settled it.
        if (transfer.HoldedPaymentRefs is { Length: > 0 })
            return new SepaBookingResult(false,
                $"An earlier attempt posted Holded payment(s) {transfer.HoldedPaymentRefs} for this "
                + "transfer and then stopped — Humans cannot see them again, so a retry would post "
                + "the money twice. Settle it in Holded by hand; nothing was posted.");

        var binding = await repo.GetCreditorContactByUserAsync(transfer.UserId, ct);
        if (binding is null || string.IsNullOrEmpty(binding.HoldedContactId))
            return new SepaBookingResult(false,
                "The member paid by that transfer has no Holded contact binding — bind them on "
                + "/Finance/Creditors first.");

        // The binding is the member's CURRENT one; the transfer names the account the file was built
        // against. A rebind between the two would pay a different creditor account than the bank
        // statement's Ustrd quotes, which is unreconcilable after the fact.
        if (binding.SupplierAccountNum != transfer.SupplierAccountNum)
            return new SepaBookingResult(false,
                $"The member's Holded binding changed since this file was generated (now "
                + $"{binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "unresolved"}, "
                + $"the transfer pays {transfer.SupplierAccountNum}) — book it by hand.");

        // Same account number is not enough: Holded lets two contacts share one 400000xx, so a
        // rebind to a sibling contact on that account slips past the check above. The file paid the
        // contact named on the transfer; anything else pays the wrong recipient's documents. Null on
        // the transfer means a row from before this guard existed — it keeps today's account-only
        // behaviour.
        if (transfer.HoldedContactId is { Length: > 0 }
            && !string.Equals(transfer.HoldedContactId, binding.HoldedContactId, StringComparison.Ordinal))
            return new SepaBookingResult(false,
                $"The member was rebound to a different Holded contact since this file was generated "
                + $"(now {binding.HoldedContactId}, the transfer paid {transfer.HoldedContactId}) — "
                + "book it by hand.");

        // ── Step 1: the bank line. It is the trigger, and its date is every posting's date. A batch
        // has already read it, and paired it with the whole file.
        HoldedBankMovementDto? movement = batch?.Movement;
        IReadOnlyList<HoldedBankMovementDto> feed = [];
        if (batch is null)
        {
            try
            {
                feed = await ReadBankFeedAsync(FeedFrom(tolerance), ct);
                movement = feed.FirstOrDefault(
                    x => string.Equals(x.Id, bankMovementId, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                logger.LogError(ex, "Could not read the Sabadell bank feed to book SEPA transfer {TransferId}.", transferId);
                return new SepaBookingResult(false,
                    "The Sabadell bank feed could not be read — nothing was posted.");
            }
        }

        if (movement is null)
            return new SepaBookingResult(false,
                $"That Sabadell line is no longer in the last {FeedWindowDays} days of the feed — "
                + "reload /Finance/Sepa.");

        // ── Step 2: re-validate the pairing here. The posted movement id is never trusted.
        var allRows = Live(await repo.GetSepaPayoutTransferRowsAsync(ct));
        if (batch is null && PairingRefusal(movement, feed, allRows) is { } pairing)
            return new SepaBookingResult(false, pairing);

        // The transfer row id, on every posting, so a Holded line traces back to one transfer — and
        // so a retry can sum what a crashed run already posted.
        var tag = "SEPA payout E" + transfer.Id.ToString("N");

        // ── Steps 3 and 4: the live creditor balance, and what this transfer already posted.
        decimal owedNow;
        decimal posted;
        try
        {
            // The live chart total, not the nightly mirror: the audit entry names what was owed now.
            owedNow = (await client.ListAccountingAccountsAsync(ct))
                .Where(a => a.Number == transfer.SupplierAccountNum)
                .Select(a => -a.Balance)
                .DefaultIfEmpty(0m)
                .First();

            // The entries feed rather than the chart: the chart can omit an entry the feed has, and
            // for this sum omission is the dangerous direction.
            //
            // The window starts at whichever is earlier, the bank line or the day the file was
            // generated, and runs to today: postings made by this flow are dated the bank line, but
            // a pre-#1185 run dated them the *click*, which can be weeks either side of it. Missing
            // one of those would re-post money that already left (nobodies-collective/Humans#1185).
            var generatedOn = allRows.FirstOrDefault(r => r.TransferId == transfer.Id)?.GeneratedAt
                .InZone(MadridZone).Date ?? movement.Date;
            var tagFrom = (generatedOn < movement.Date ? generatedOn : movement.Date)
                .PlusDays(-TagWindowDays);
            var tagTo = Today().PlusDays(TagWindowDays);
            posted = (await client.ListLedgerEntriesAsync(
                    tagFrom, tagTo, transfer.SupplierAccountNum, ct))
                // The account filter is the server's; asserting it here too keeps a dropped query
                // parameter from netting our own two-sided entry to zero and re-posting everything.
                .Where(l => l.AccountNum == transfer.SupplierAccountNum)
                .Where(l => (l.Description ?? "").Contains(tag, StringComparison.Ordinal))
                .Sum(l => l.Debit - l.Credit);   // paying a creditor DEBITS 400xxxxx
            if (posted < 0m) posted = 0m;        // a tagged credit is not "already paid"
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Could not read Holded's creditor balance to book SEPA transfer {TransferId}.", transferId);
            return new SepaBookingResult(false,
                "Holded's creditor balance could not be read — nothing was posted.");
        }

        // ── Step 5: the arithmetic. The whole transfer, less what is already posted — never capped
        // by what the account owes now. The file went to the bank and the money left, so the ledger
        // has to show it whatever the balance says: an account that owed less than the transfer
        // (two files from one balance, documents hand-paid in between) simply ends up overpaid,
        // which the audit entry names and the next creditor read shows as a negative balance.
        //
        // Rounded to cents once, here: every posting is sent to Holded formatted to two decimals, so
        // an unrounded total would let each document round up on its own and the total drift over.
        var toPost = Math.Round(transfer.Amount - posted, 2, MidpointRounding.AwayFromZero);
        var resumed = posted > CentEpsilon;
        var overpaidBy = Math.Round(toPost - owedNow, 2, MidpointRounding.AwayFromZero);

        var paidDocs = new List<(string DocId, decimal Amount, string Ref)>();
        string? entryRef = null;
        var entryAmount = 0m;
        var remaining = toPost;

        if (remaining > CentEpsilon)
        {
            IReadOnlyList<HoldedPurchaseDocListItemDto> open;
            try
            {
                open = OpenDocs(await client.ListPurchaseDocumentsAsync(ct))
                    .Where(d => string.Equals(d.ContactId, binding.HoldedContactId, StringComparison.Ordinal))
                    // Oldest first: the money the member has been owed longest clears first.
                    .OrderBy(d => d.Date)
                    .ThenBy(d => d.Id, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                logger.LogError(ex, "Could not read Holded purchase documents to book SEPA transfer {TransferId}.", transferId);
                return new SepaBookingResult(false, "Holded's purchase documents could not be read — nothing was posted.");
            }

            // ── Step 6: FIFO document payments, every one dated the bank line. PaymentsPending is
            // read live, so a document an earlier run already paid shows 0 and is skipped — that is
            // what makes this pass idempotent.
            foreach (var doc in open)
            {
                if (remaining <= CentEpsilon) break;
                var amount = Math.Round(
                    Math.Min(remaining, doc.PaymentsPending), 2, MidpointRounding.AwayFromZero);
                if (amount <= 0m) continue;

                try
                {
                    paidDocs.Add((doc.Id, amount, await client.PayPurchaseDocumentAsync(
                        doc.Id, amount, sepa.Value.TreasuryAccountId, movement.Date, tag, ct)));
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    return await RefusedMidBookingAsync(ex, "payment", "Holded document " + doc.Id);
                }

                remaining -= amount;
            }

            // ── Step 7: whatever the documents did not cover — a loan, a balance with no document
            // behind it — as one journal entry: debit 400xxxxx, credit the bank.
            if (remaining > CentEpsilon)
            {
                try
                {
                    entryAmount = remaining;
                    entryRef = await client.PostLedgerEntryAsync(
                        movement.Date, transfer.SupplierAccountNum, sepa.Value.TreasuryLedgerAccount!.Value,
                        remaining, tag, ct);
                    remaining -= entryAmount;   // what is still unposted, which is now nothing
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    entryAmount = 0m;
                    return await RefusedMidBookingAsync(
                        ex, "journal entry", "creditor account " + transfer.SupplierAccountNum);
                }
            }
        }

        // ── Step 8: persist, then reconcile, then stamp. Deliberately not the other order — losing
        // the local save after Holded took the money is nobodies-collective/Humans#1185 itself.
        var now = clock.GetCurrentInstant();
        // The journal remainder rides along as an `entry:<amount>` token: not a Holded id, never
        // sent in a reconcile, but what lets the row say how much of the line a human has to match.
        var refTokens = paidDocs.Select(p => p.Ref).ToList();
        if (entryRef is not null)
            refTokens.Add(SepaPayoutTransferRow.JournalEntryRefPrefix
                          + entryAmount.ToString("F2", CultureInfo.InvariantCulture));
        var refs = refTokens.Count == 0 ? null : string.Join(",", refTokens);
        try
        {
            await repo.SaveSepaTransferBookingAsync(transfer.Id, now, actorUserId, movement.Id, null, refs, ct);
        }
        catch (Exception ex)
        {
            // The money has moved and the row does not say so — the one state that must never be
            // invisible. Audit it, then leave the row retryable.
            logger.LogError(ex,
                "Holded accepted every posting for SEPA transfer {TransferId} but the booking row could not be saved.",
                transferId);
            await RememberLandedPaymentsAsync();
            await AuditBookingAsync(
                $"PARTIAL SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on "
                + $"creditor account {transfer.SupplierAccountNum} against Sabadell line "
                + $"{movement.Id}: Holded accepted {PostingSummary()} and then the transfer row "
                + $"could not be saved. The transfer is NOT booked; {NextStep()}.");
            return new SepaBookingResult(false,
                "Holded accepted the postings but the transfer row could not be saved — it is NOT "
                + $"marked booked; {NextStep()}.");
        }

        // The reconcile names the PAYMENTS, never the documents: a purchase ref makes Holded mint a
        // new payment for the whole line on that document. A payment Holded gave no id back for
        // cannot be named, and a journal entry cannot be linked to a bank line at all — either
        // leaves part of the line's money unnamed, so Holded may read it `partial` afterwards.
        var docs = PaymentRefs(refs);
        var incomplete = docs.Count < paidDocs.Count || entryRef is not null || resumed;

        var summary = PostingSummary();
        var resumedPart = resumed ? $"; resumed ({Euros(posted)} already posted)" : "";
        // The Board's view of a transfer that paid more than the account owed: named here, since
        // nothing refused it and the balance alone will not say which transfer did it.
        var overpaidPart = toPost > CentEpsilon && overpaidBy > CentEpsilon
            ? $"; OVERPAID — the account owed {Euros(Math.Max(0m, owedNow))}, so this leaves it "
              + $"{Euros(overpaidBy)} in credit"
            : "";

        // The line is shared with the rest of the file: it is reconciled once, by the batch, after
        // the last transfer — a reconcile now would name only this transfer's part of it.
        if (batch is not null)
        {
            batch.Docs.AddRange(docs);
            if (incomplete) batch.Incomplete = true;
            await AuditBookingAsync(
                $"Booked SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
                + $"{transfer.SupplierAccountNum} against Sabadell line {movement.Id} dated "
                + $"{movement.Date.ToInvariantDate()}, one of {batch.TransferCount} transfers the bank "
                + $"debited as that one line ({Euros(movement.Amount)}): {summary}{resumedPart}"
                + $"{overpaidPart}; the line is reconciled once the whole file is booked.");
            return new SepaBookingResult(true, $"Booked {Euros(transfer.Amount)}: {summary}.");
        }

        var reconciledAt = await TryReconcileAsync(movement.Id, movement.Date, docs, incomplete, ct);
        if (reconciledAt is not null)
            await repo.MarkSepaTransferReconciledAsync(transfer.Id, reconciledAt.Value, ct);

        // ── Step 9: audit, after the save, naming every Holded id this run created.
        var reconcilePart = reconciledAt is not null
            ? "reconciled."
            : entryRef is not null
                ? "RECONCILE PENDING — the journal entry cannot be linked to the bank line by API; "
                  + "reconcile the line in Holded by hand."
                : "RECONCILE PENDING — tick it in Holded.";
        var description =
            $"Booked SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
            + $"{transfer.SupplierAccountNum} against Sabadell line {movement.Id} dated "
            + $"{movement.Date.ToInvariantDate()}: {summary}"
            + $"{resumedPart}{overpaidPart}; {reconcilePart}";

        await AuditBookingAsync(description);

        return new SepaBookingResult(true, $"Booked {Euros(transfer.Amount)}: {summary}.");

        // One booking audit entry, under the acting admin or under the sweep's job name.
        Task AuditBookingAsync(string text) =>
            actorUserId is { } actor
                ? audit.LogAsync(
                    AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, transfer.Id,
                    text, actor, transfer.UserId, nameof(User))
                : audit.LogAsync(
                    AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, transfer.Id,
                    text, SepaBookingJobName, transfer.UserId, nameof(User));

        string PostingSummary()
        {
            var docPart = paidDocs.Count == 0
                ? "0 document payment(s)"
                : $"{paidDocs.Count} document payment(s) ("
                  + string.Join(", ", paidDocs.Select(p => $"{p.DocId} {Euros(p.Amount)} {p.Ref}")) + ")";
            var entryPart = entryRef is null
                ? ""
                : $" and a journal entry for {Euros(entryAmount)} ({entryRef})";
            return docPart + entryPart;
        }

        // Whatever Holded already accepted is real money and is kept, but nothing is written to the
        // transfer row: the next attempt re-reads the tag sum and posts only the difference. That
        // resumability is the whole point of nobodies-collective/Humans#1185 — a partial booking is
        // never terminal.
        async Task<SepaBookingResult> RefusedMidBookingAsync(Exception ex, string what, string where)
        {
            var landed = PostingSummary();
            if (paidDocs.Count > 0)
            {
                await RememberLandedPaymentsAsync();
                // Postings an automation caused, so they get an audit row on this path too — same
                // action as a completed booking, labelled PARTIAL.
                var partial =
                    $"PARTIAL SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on "
                    + $"creditor account {transfer.SupplierAccountNum} against Sabadell line "
                    + $"{movement.Id}: Holded accepted {landed} and then refused the {what}. The "
                    + $"transfer is NOT booked; {NextStep()}.";
                await AuditBookingAsync(partial);
            }

            logger.LogError(ex,
                "Booking SEPA transfer {TransferId} failed after {Posted} posting(s), on the {What} for {Where}.",
                transferId, paidDocs.Count, what, where);

            if (paidDocs.Count > 0)
                return new SepaBookingResult(false,
                    $"Holded accepted {paidDocs.Count} posting(s) and then refused the {what}; the "
                    + $"transfer is NOT marked booked; {NextStep()}.");

            // An accepted-but-unreadable posting comes back as an "unconfirmed:" ref rather than an
            // exception, so reaching here on the first one means Holded really refused it.
            return new SepaBookingResult(false, $"Holded refused the {what} — nothing was posted.");
        }

        // What a run that is not booking leaves the treasurer with. A journal entry alone keeps
        // its tag, so the tag sum resumes from it; a document payment does not, so the retry refuses.
        string NextStep() => paidDocs.Count == 0
            ? "the next attempt posts only what is still missing"
            : "settle it in Holded by hand — a retry refuses, since Holded keeps no tag on a payment";

        // Money that reached Holded on a run that is not booking: written to the row so the next
        // attempt refuses (the tag sum cannot see a purchase payment) and the page says why.
        async Task RememberLandedPaymentsAsync()
        {
            if (paidDocs.Count == 0) return;
            try
            {
                await repo.SaveSepaTransferPaymentRefsAsync(
                    transfer.Id, string.Join(",", paidDocs.Select(p => p.Ref)), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "The payment ids Holded accepted for SEPA transfer {TransferId} could not be saved; the "
                    + "audit entry is the only record of them.", transferId);
            }
        }

        string? PairingRefusal(
            HoldedBankMovementDto m, IReadOnlyList<HoldedBankMovementDto> lines,
            IReadOnlyList<SepaPayoutTransferRow> all)
        {
            if (m.Amount >= 0m || Math.Abs(m.Amount) != transfer.Amount)
                return $"That Sabadell line is {Euros(m.Amount)}, not the {Euros(transfer.Amount)} this "
                       + "transfer pays — nothing was posted.";
            if (IsReconciled(m))
                return "That Sabadell line is already reconciled in Holded — nothing was posted.";
            // Part of that line is already settled against documents by hand; posting the whole
            // transfer on top of it would pay some of them twice.
            if (!IsUntouched(m))
                return $"That Sabadell line is already {m.Status} in Holded — settle it there by hand. "
                       + "Nothing was posted.";

            // Text naming another account is another transfer's line. No text at all is how
            // Sabadell books these, so the line is matched on amount and date instead.
            var account = RemittanceAccountNum(m.Description);
            if (account is not null && account.Value != transfer.SupplierAccountNum)
                return $"That Sabadell line's text names creditor account {account.Value}, not "
                       + $"{transfer.SupplierAccountNum} — nothing was posted.";
            if (account is null && TextlessLineFitsAFile(all, m, tolerance))
                return "That Sabadell line names no creditor account and could be a whole file's debit "
                       + "as well as this transfer's — settle it in Holded by hand. Nothing was posted.";

            // One bank line settles one transfer.
            if (all.Any(r => r.TransferId != transfer.Id
                             && string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
                return "That Sabadell line already booked another transfer — nothing was posted.";

            var matches = UnbookedMatches(all, account, transfer.Amount, m.Date, tolerance);
            if (matches.Count > 1)
            {
                // The page's set pairing, re-run here: the set's own rival check stands in for the
                // one below.
                var own = account is null
                    ? TextlessSetPairs(all, lines, m, tolerance)
                        .FirstOrDefault(p => string.Equals(p.Line.Id, m.Id, StringComparison.Ordinal)).Row
                    : null;
                if (own is null)
                    return $"{matches.Count} unbooked transfers match that Sabadell line — Humans cannot tell "
                           + "which it paid; settle it in Holded by hand. Nothing was posted.";
                return own.TransferId == transfer.Id
                    ? null
                    : "That Sabadell line does not match this transfer — nothing was posted.";
            }
            if (matches.Count != 1 || matches[0].TransferId != transfer.Id)
                return "That Sabadell line does not match this transfer — nothing was posted.";

            // The mirror: another unreconciled line of the same amount, dated no earlier than this
            // file, that names this account or no account at all, could equally be the one that
            // paid it.
            var rivals = lines.Count(x =>
                !string.Equals(x.Id, m.Id, StringComparison.Ordinal)
                && NeedsAMatch(x)
                && Math.Abs(x.Amount) == transfer.Amount
                && RemittanceAccountNum(x.Description) is var xAccount
                && (xAccount is null || xAccount == transfer.SupplierAccountNum)
                && !all.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal))
                && UnbookedMatches(all, xAccount, transfer.Amount, x.Date, tolerance)
                    .Any(r => r.TransferId == transfer.Id));
            return rivals == 0
                ? null
                : $"{rivals + 1} Sabadell lines could each have paid this transfer — Humans cannot tell "
                  + "which one did; settle it in Holded by hand. Nothing was posted.";
        }
    }

    /// <summary>Tells Holded the bank line and the payments are the same money. When the run could
    /// name every euro of the line, Holded accepting the call is the reconcile; when it could not —
    /// a journal remainder, an unconfirmed payment, a resumed or re-issued booking — it only stamps
    /// when Holded then reports the line <c>reconciled</c>, since what was left out can leave it
    /// <c>partial</c>. Anything else leaves the booking standing and the row "reconcile pending",
    /// which the page shows and the sweep re-issues.
    /// The ids go in one call first; a long list Holded refuses outright is re-sent in chunks of
    /// <see cref="ReconcileChunkSize"/> against the same line, and after chunks the line is always
    /// re-read — only Holded saying <c>reconciled</c> stamps it.</summary>
    private async Task<Instant?> TryReconcileAsync(
        string movementId, LocalDate movementDate, List<HoldedReconcileDocumentRef> docs,
        bool incomplete, CancellationToken ct)
    {
        if (docs.Count == 0) return null;

        var chunked = false;
        try
        {
            await client.ReconcileBankMovementAsync(sepa.Value.TreasuryAccountId, movementId, docs, ct);
        }
        catch (HoldedPermanentException ex) when (docs.Count > ReconcileChunkSize)
        {
            // A refusal moved no money and linked nothing, so the same ids can go again, shorter.
            logger.LogWarning(ex,
                "Holded refused the {Count}-payment reconcile of bank movement {MovementId} in one call; "
                + "re-sending it in chunks of {Chunk}.", docs.Count, movementId, ReconcileChunkSize);
            chunked = true;
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Holded refused to reconcile bank movement {MovementId}.", movementId);
            return null;
        }

        var refused = new List<HoldedReconcileDocumentRef>();
        if (chunked)
        {
            // A refused chunk does not stop the rest: the chunks already through are linked, and
            // one bad id must not strand the payments behind it. What Holded refused is named below.
            var number = 0;
            foreach (var chunk in docs.Chunk(ReconcileChunkSize))
            {
                number++;
                try
                {
                    await client.ReconcileBankMovementAsync(sepa.Value.TreasuryAccountId, movementId, chunk, ct);
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    logger.LogWarning(ex,
                        "Holded refused chunk {Chunk} ({Size} payment(s)) of the reconcile of bank movement "
                        + "{MovementId}.", number, chunk.Length, movementId);
                    refused.AddRange(chunk);
                }
            }

            if (refused.Count > 0 && refused.Count < docs.Count)
                // The line is now `partial`, which the sweep never re-issues (its payments are
                // linked, and Holded does not say which): the refused ids are a human's to finish.
                logger.LogWarning(
                    "Holded linked {Linked} of {Count} payment(s) to bank movement {MovementId} and refused "
                    + "{Refused}: {Ids}. The line reads partial, which the sweep does not re-issue — finish "
                    + "it in Holded by hand.",
                    docs.Count - refused.Count, docs.Count, movementId, refused.Count,
                    string.Join(",", refused.Select(d => d.DocumentId)));
        }

        return incomplete || chunked
            ? await StampIfReconciledAsync(movementId, movementDate, docs, incomplete || refused.Count > 0, ct)
            : clock.GetCurrentInstant();
    }

    /// <summary>The reconcilable refs in a row's comma-joined payment ids: an <c>unconfirmed:</c>
    /// sentinel is not a Holded id and is left out, never replaced by the document id; the
    /// <c>entry:</c> token that records a journal remainder is not a treasury object and is left out
    /// too — Holded cannot link a ledger entry to a bank line.</summary>
    private static List<HoldedReconcileDocumentRef> PaymentRefs(string? joined) =>
        (joined ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => !r.StartsWith("unconfirmed:", StringComparison.Ordinal)
                        && !r.StartsWith(SepaPayoutTransferRow.JournalEntryRefPrefix, StringComparison.Ordinal))
            .Select(r => new HoldedReconcileDocumentRef(r, HoldedReconcileDocumentType.Payment))
            .ToList();

    /// <summary>The stamp for a reconcile that could not be trusted on acceptance alone: one that left
    /// a journal remainder out, or went through in chunks. ReconciledAt means Holded says
    /// <c>reconciled</c> — so ask it; anything else (including a feed the re-read cannot get) stays
    /// reconcile pending. A line still <c>partial</c> after every euro was named
    /// (<paramref name="incomplete"/> false) is a real finding and is logged as one, with what was
    /// sent; a remainder the run knew it left out is only noted.</summary>
    private async Task<Instant?> StampIfReconciledAsync(
        string movementId, LocalDate movementDate, List<HoldedReconcileDocumentRef> docs,
        bool incomplete, CancellationToken ct)
    {
        HoldedBankMovementDto? line;
        try
        {
            var after = await client.ListBankMovementsAsync(
                sepa.Value.TreasuryAccountId, movementDate, movementDate, ct, SkippedStuckLineIds());
            line = after.FirstOrDefault(m => string.Equals(m.Id, movementId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex,
                "Could not re-read bank movement {MovementId} after reconciling it; the transfer stays "
                + "reconcile pending.", movementId);
            return null;
        }

        if (line is not null && IsReconciled(line)) return clock.GetCurrentInstant();

        if (line is not null && !incomplete && !IsUntouched(line))
            logger.LogWarning(
                "Holded took every payment of bank movement {MovementId} ({Amount}) but the line reads "
                + "{Status}; the {Count} id(s) sent: {Ids}. The transfer stays reconcile pending — "
                + "settle the line in Holded by hand.",
                movementId, Euros(line.Amount), line.Status, docs.Count,
                string.Join(",", docs.Select(d => d.DocumentId)));
        else
            logger.LogInformation(
                "Holded took the reconcile for bank movement {MovementId} but the line reads {Status}; the "
                + "transfer stays reconcile pending.", movementId,
                line?.Status ?? "gone");
        return null;
    }

    private LocalDate Today() => clock.GetCurrentInstant().InZone(MadridZone).Date;

    /// <summary>The first day the feed is read from for matching: the oldest non-stale file's first
    /// payable day under <paramref name="tolerance"/>, so every line <see cref="UnbookedMatches"/>
    /// could accept is on it.</summary>
    private LocalDate FeedFrom(int tolerance) => Today().PlusDays(-FeedWindowDays - tolerance);

    /// <summary>Reads the feed, skipping the lines the sweep has given up on
    /// (<see cref="SkippedStuckLineIds"/>) unless the caller — the sweep's own counting read — names
    /// the set itself.</summary>
    private Task<IReadOnlyList<HoldedBankMovementDto>> ReadBankFeedAsync(
        LocalDate from, CancellationToken ct, IReadOnlySet<string>? skipUnreadableDateIds = null) =>
        client.ListBankMovementsAsync(
            sepa.Value.TreasuryAccountId, from, Today(), ct,
            skipUnreadableDateIds ?? SkippedStuckLineIds());

    /// <summary>The stuck lines that have failed <see cref="StuckLineSkipAfterRuns"/> runs in a row.
    /// Every other read of the feed — the booking's own re-read, the page, the post-reconcile check —
    /// skips them too, or the booking the sweep just unblocked would fail on the same line.</summary>
    private HashSet<string> SkippedStuckLineIds() =>
        bookingState.TryGet(CacheKeys.SepaStuckBankLines, out var runs)
            ? runs.Where(kv => kv.Value >= StuckLineSkipAfterRuns).Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>An outgoing line Holded has not started reconciling — the only kind that can still
    /// be a SEPA payout waiting to be booked. <c>partial</c> counts as started: somebody has already
    /// settled part of that line against documents by hand, so posting the whole transfer on top of
    /// it would pay those documents twice.</summary>
    private static bool NeedsAMatch(HoldedBankMovementDto m) => m.Amount < 0m && IsUntouched(m);

    /// <summary>Holded reports <c>pending</c> | <c>partial</c> | <c>reconciled</c>; only the first
    /// has nothing of ours or anyone else's already tied to it.</summary>
    private static bool IsUntouched(HoldedBankMovementDto m) =>
        string.Equals(m.Status, PendingStatus, StringComparison.Ordinal);

    private static bool IsReconciled(HoldedBankMovementDto m) =>
        string.Equals(m.Status, ReconciledStatus, StringComparison.Ordinal);

    /// <summary>The 400000xx the payout file wrote into the remittance text, or null when the line
    /// is not one of ours.</summary>
    private static int? RemittanceAccountNum(string? description)
    {
        var match = RemittanceAccount.Match(description ?? "");
        return match.Success
            && int.TryParse(match.Groups["acct"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
            ? num
            : null;
    }

    /// <summary>The rows any matcher may see: a withdrawn transfer was cancelled at the bank, so no
    /// line is its payment and no file total includes it. Every pairing path reads through this.</summary>
    private static List<SepaPayoutTransferRow> Live(IEnumerable<SepaPayoutTransferRow> rows) =>
        rows.Where(r => !r.IsWithdrawn).ToList();

    /// <summary>The unbooked transfers a bank line dated <paramref name="lineDate"/> for
    /// <paramref name="account"/> and <paramref name="amount"/> could be paying — any account when
    /// the line's text names none, since Sabadell drops the remittance text and the amount and date
    /// are all a line carries. More than one is ambiguous and waits for a human. Only files generated inside the feed window count: a stale
    /// row from an old file would otherwise make every later transfer of the same amount permanently
    /// ambiguous, and no live bank line can be paying it anyway. A line dated before the file that
    /// asked for it cannot be paying it either — that one is some older payment of the same amount,
    /// still unreconciled on the feed.</summary>
    private List<SepaPayoutTransferRow> UnbookedMatches(
        IReadOnlyList<SepaPayoutTransferRow> rows, int? account, decimal amount, LocalDate lineDate,
        int tolerance) =>
        rows.Where(r => r.BookedAt is null && !r.IsWithdrawn && (account is null || r.SupplierAccountNum == account)
                        && r.Amount == amount && !IsStale(r)
                        && InWindow(r, lineDate, tolerance, bounded: account is null))
            .ToList();

    /// <summary>Whether a line dated <paramref name="lineDate"/> is close enough to
    /// <paramref name="row"/>'s file date to be its payment: no more than <paramref name="tolerance"/>
    /// days before it, and — for a line the date has to carry (<paramref name="bounded"/>, a
    /// textless one) — no more than that many days after it either. A line whose text names the
    /// account is tied by the text, so later is still fine.</summary>
    private bool InWindow(SepaPayoutTransferRow row, LocalDate lineDate, int tolerance, bool bounded)
    {
        var fileDate = FileDate(row);
        return lineDate >= fileDate.PlusDays(-tolerance)
               && (!bounded || lineDate <= fileDate.PlusDays(tolerance));
    }

    /// <summary>A textless line whose amount is also some multi-transfer file's total, dated inside
    /// that file's window with transfers still unbooked: Humans cannot tell one transfer's debit from
    /// a whole file's, so neither path takes it.</summary>
    private bool TextlessLineFitsAFile(
        IReadOnlyList<SepaPayoutTransferRow> rows, HoldedBankMovementDto m, int tolerance) =>
        rows.Where(r => !r.IsWithdrawn).GroupBy(r => r.FileId).Any(f =>
            f.Count() >= 2 && f.Sum(r => r.Amount) == Math.Abs(m.Amount)
            && f.Any(r => !r.IsBooked) && !IsStale(f.First())
            // A row booked against some other line means the bank did not debit this file as
            // one — the same rule BatchLineRefusal applies.
            && !f.Any(r => r.IsBooked && !string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal))
            && InWindow(f.First(), m.Date, tolerance, bounded: true));

    /// <summary>The one-to-one pairing of a textless line's set: the unbooked transfers of its amount
    /// — two or more, all of one file — with the textless lines that match exactly those transfers,
    /// when the two counts are equal and no other unbooked line could be paying any of them. Which
    /// line paid which transfer is then unknowable and accounting-neutral (each transfer books its
    /// own creditor account; the lines carry no text), so the assignment is simply line by id
    /// against transfer by creditor account. Empty when the set does not pair: unequal counts, two
    /// files, a named line or a whole-file candidate among the lines.</summary>
    private List<(HoldedBankMovementDto Line, SepaPayoutTransferRow Row)> TextlessSetPairs(
        IReadOnlyList<SepaPayoutTransferRow> rows, IReadOnlyList<HoldedBankMovementDto> feed,
        HoldedBankMovementDto m, int tolerance)
    {
        var set = UnbookedMatches(rows, null, Math.Abs(m.Amount), m.Date, tolerance);
        if (set.Count < 2 || set.Select(r => r.FileId).Distinct().Count() > 1) return [];
        var ids = set.Select(r => r.TransferId).ToHashSet();
        var claimants = feed
            .Where(x => NeedsAMatch(x)
                        && !rows.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal)))
            .Select(x => (Line: x, Account: RemittanceAccountNum(x.Description)))
            .Select(x => (x.Line, x.Account, Matches:
                UnbookedMatches(rows, x.Account, Math.Abs(x.Line.Amount), x.Line.Date, tolerance)
                    .Select(r => r.TransferId).ToHashSet()))
            .Where(x => x.Matches.Overlaps(ids))
            .ToList();
        if (claimants.Count != set.Count
            || claimants.Any(x => x.Account is not null || !x.Matches.SetEquals(ids)
                                  || TextlessLineFitsAFile(rows, x.Line, tolerance)))
            return [];
        return claimants
            .OrderBy(x => x.Line.Id, StringComparer.Ordinal)
            .Zip(set.OrderBy(r => r.SupplierAccountNum), (x, r) => (x.Line, r))
            .ToList();
    }

    /// <summary>The files whose whole total <paramref name="m"/> could be the one debit of. More than
    /// one is ambiguous and waits for a human.</summary>
    private List<Guid> BatchFiles(
        IReadOnlyList<SepaPayoutTransferRow> rows, HoldedBankMovementDto m,
        IReadOnlyList<HoldedBankMovementDto> feed, int tolerance) =>
        rows.Where(r => !r.IsWithdrawn).GroupBy(r => r.FileId)
            .Where(g => BatchLineRefusal(g.ToList(), m, rows, feed, tolerance) is null)
            .Select(g => g.Key)
            .ToList();

    /// <summary>Why <paramref name="m"/> cannot be the single bank debit for every transfer in
    /// <paramref name="file"/>, or null when it can — judged on this line and this file alone; other
    /// files and other lines of the same total are the caller's ambiguity check. The amount must be
    /// the file's total, exactly: nothing is ever split by guesswork.</summary>
    private string? BatchLineRefusal(
        IReadOnlyList<SepaPayoutTransferRow> file, HoldedBankMovementDto m,
        IReadOnlyList<SepaPayoutTransferRow> rows, IReadOnlyList<HoldedBankMovementDto> feed,
        int tolerance)
    {
        var total = file.Sum(r => r.Amount);
        if (file.Count < 2)
            return "That file holds one transfer — book it on its own row. Nothing was posted.";
        if (m.Amount >= 0m || Math.Abs(m.Amount) != total)
            return $"That Sabadell line is {Euros(m.Amount)}, not the {Euros(total)} this file totals — "
                   + "nothing was posted.";
        if (!IsUntouched(m))
            return $"That Sabadell line is already {m.Status} in Holded — settle it there by hand. "
                   + "Nothing was posted.";
        if (IsStale(file[0]))
            return $"That file was generated more than {FeedWindowDays} days ago — settle it in Holded "
                   + "by hand. Nothing was posted.";
        if (!InWindow(file[0], m.Date, tolerance, bounded: true))
            return $"That Sabadell line is dated more than {tolerance} day(s) from when this file was "
                   + "generated — widen the tolerance if it is this file's. Nothing was posted.";
        if (file.All(r => r.IsBooked))
            return "Every transfer in that file is already booked — nothing was posted.";
        // A transfer booked against a line of its own means the bank did not debit this file as one.
        if (file.Any(r => r.IsBooked
                          && !string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            return "Part of that file was already booked against its own Sabadell lines — settle the "
                   + "rest in Holded by hand. Nothing was posted.";
        if (rows.Any(r => r.FileId != file[0].FileId
                          && string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            return "That Sabadell line already booked a transfer from another file — nothing was posted.";
        // A line whose text names one transfer's account and amount is that transfer's own line.
        if (RemittanceAccountNum(m.Description) is { } account)
        {
            if (UnbookedMatches(rows, account, Math.Abs(m.Amount), m.Date, tolerance).Count > 0)
                return "That Sabadell line names a single transfer — book it on that transfer's row. "
                       + "Nothing was posted.";
        }
        // A textless one that is also some single transfer's amount could be either.
        else if (UnbookedMatches(rows, null, Math.Abs(m.Amount), m.Date, tolerance).Count > 0)
            return "That Sabadell line names no creditor account and could be a single transfer's "
                   + "debit as well as this file's — settle it in Holded by hand. Nothing was posted.";
        // And the mirror: a transfer of this file with a line of its own was not debited as part of
        // one total, so a total-sized line is some other payment.
        if (feed.Any(x => NeedsAMatch(x)
                          && !rows.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal))
                          && RemittanceAccountNum(x.Description) is { } own
                          && UnbookedMatches(file, own, Math.Abs(x.Amount), x.Date, tolerance).Count > 0))
            return "A transfer in that file has a Sabadell line of its own — book the transfers on their "
                   + "rows. Nothing was posted.";
        return null;
    }

    /// <summary>The earliest bank date that can belong to a transfer: its file's generation day,
    /// less <see cref="GenerationSlackDays"/>.</summary>
    private LocalDate FirstPayableDate(SepaPayoutTransferRow row) =>
        FileDate(row).PlusDays(-GenerationSlackDays);

    /// <summary>The day the file was generated, in the org's zone — what a bank line's date is
    /// measured against.</summary>
    internal LocalDate FileDate(SepaPayoutTransferRow row) => row.GeneratedAt.InZone(MadridZone).Date;

    /// <summary>A transfer whose file was generated before the bank feed's window — no line the feed
    /// still returns can book it, so it is a human's job in Holded.</summary>
    internal bool IsStale(SepaPayoutTransferRow row) =>
        row.GeneratedAt.InZone(MadridZone).Date < Today().PlusDays(-FeedWindowDays);

    /// <summary>Why booking is unavailable for every row at once, or null. The organisation's SEPA
    /// identity is required because it is what generated the transfers; the treasury account because
    /// omitting it lets Holded pay from whichever account it defaults to; its ledger account because
    /// the journal entry for an uncovered remainder has to credit the same bank.</summary>
    private string? BookingUnavailableReason()
    {
        if (sepa.Value.GetPayoutSettings().UnavailableReason is { } sepaMissing) return sepaMissing;
        if (string.IsNullOrWhiteSpace(sepa.Value.TreasuryAccountId))
            return "Booking is unavailable — Sepa:TreasuryAccountId is not configured.";
        return sepa.Value.TreasuryLedgerAccount is null
            ? "Booking is unavailable — Sepa:TreasuryLedgerAccount is not configured."
            : null;
    }

    /// <summary>Approved purchase documents that still owe something. A draft books nothing to the
    /// ledger, so paying one would post against a document that does not exist for accounting.</summary>
    internal static List<HoldedPurchaseDocListItemDto> OpenDocs(
        IReadOnlyList<HoldedPurchaseDocListItemDto> docs) =>
        docs.Where(d => d.IsDraft == false && d.PaymentsPending > 0m).ToList();

}
