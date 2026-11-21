using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Humans.Auth.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Constants;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Jobs;
using Humans.Gate.Services.Stores;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Gate.Services;

internal sealed class GateService(
    IGateRepository repository,
    ITicketService tickets,
    IEarlyEntryService earlyEntry,
    ISettingsService settingsService,
    IShiftService shifts,
    IRoleAssignmentService roles,
    ITicketWrite ticketWrite,
    ITicketGateLookup issuedTickets,
    IPasswordHasher<GateStaffPin> pinHasher,
    IAuditLogService auditLog,
    IBackgroundJobClient backgroundJobs,
    GateVendorMirrorLedger mirrorLedger,
    GatePinThrottle pinThrottle,
    IConfiguration configuration,
    ILogger<GateService> logger,
    IClock clock,
    IUserService users) : IGateService, IUserMerge, IUserDataContributor
{
    internal const string GateScans = "GateScans";

    private const string SupervisorPinConfigKey = "Gate:SupervisorPin";
    private const string SharedOverridePinKey = "override:shared";

    private const string VendorMirrorEnabledKey = "Gate:VendorMirrorEnabled";

    /// <summary>How far either side of now a gate shift may start to count as current.</summary>
    private static readonly Duration RosterWindow = Duration.FromHours(2);

    /// <summary>Server-verified, never client-asserted. Board reads the gate and authorizes no override.</summary>
    private static readonly string[] SupervisorRoles = [RoleNames.Admin, TicketsRoles.TicketsAdmin];

    public async Task<GateScanResult> EvaluateAsync(string barcode, CancellationToken ct = default)
    {
        var code = GateBarcode.Normalize(barcode);
        if (code.Length == 0)
            return NotFound(code);

        var burn = settingsService.GetActiveEventSettings();
        if (await FindTicketAsync(code, burn, ct) is not { } ticket)
            return NotFound(code);

        var settings = await repository.GetSettingsAsync(ct);
        var priorAdmit = await repository.GetAdmitForBarcodeAsync(code, ct);
        var now = clock.GetCurrentInstant();
        var zone = EventZone(burn?.TimeZoneId);
        var today = now.InZone(zone).Date;

        EarlyEntryRosterRow? ee = ticket.EntryUserId is { } uid
            ? await earlyEntry.GetForUserAsync(uid, ct)
            : null;

        var outcome = GateAdmissionRules.Evaluate(new GateScanContext(
            Found: true,
            IsVoid: ticket.IsVoid,
            AlreadyAdmittedLocally: priorAdmit is not null,
            CheckedInAtVendor: ticket.CheckedInAtVendor,
            Now: now,
            GeneralEntryOpensAt: settings.IsCutoffConfigured ? settings.GeneralEntryOpensAt : null,
            MatchedToHuman: ticket.EntryUserId is not null,
            EarliestEntryDate: ee?.EarliestEntryDate,
            Today: today));

        return new GateScanResult(
            outcome,
            code,
            ticket.Name,
            ticket.TypeName,
            IsEarly: outcome == GatePreCheckOutcome.NeedsIdCheckEarly,
            EarlyEntrySource: ee is { Sources.Count: > 0 } ? string.Join(", ", ee.Sources) : null,
            TicketAttendeeId: ticket.TicketAttendeeId,
            GuestUserId: ticket.GuestUserId,
            PreviousAdmitAt: priorAdmit?.OccurredAt,
            PreviousAdmitByUserId: priorAdmit?.ScannedByUserId,
            VendorTicketId: ticket.VendorTicketId,
            // Date only, never the EE source (privacy).
            EarliestEntryDate: ee?.EarliestEntryDate,
            Today: today,
            GeneralEntryDate: settings.IsCutoffConfigured ? settings.GeneralEntryOpensAt.InZone(zone).Date : null)
        {
            CheckoutTicketId = ticket.CheckoutTicketId,
            InResaleQueue = ticket.InResaleQueue,
        };
    }

    /// <summary>
    /// A Ticket Tailor ticket of the current event, else a ticket Humans' checkout issued for the active event. Either
    /// way Gate's own scan row is the check-in record; only Ticket Tailor tickets carry a vendor check-in to respect.
    /// </summary>
    private async Task<ScannedTicket?> FindTicketAsync(string code, EventSettingsInfo? burn, CancellationToken ct)
    {
        var orders = await tickets.GetTicketOrdersAsync(ct);
        var attendee = orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => a.Barcode is not null
                && string.Equals(GateBarcode.Normalize(a.Barcode), code, StringComparison.Ordinal));
        if (attendee is not null)
            return new ScannedTicket(
                attendee.AttendeeName, attendee.TicketTypeName,
                IsVoid: attendee.Status == TicketAttendeeStatus.Void,
                CheckedInAtVendor: attendee.CheckedInAt is not null || attendee.Status == TicketAttendeeStatus.CheckedIn,
                GuestUserId: attendee.MatchedUserId, EntryUserId: attendee.MatchedUserId,
                TicketAttendeeId: attendee.Id, CheckoutTicketId: null, VendorTicketId: attendee.VendorTicketId,
                InResaleQueue: false);

        if (burn is null || await issuedTickets.FindIssuedTicketAsync(code, ct) is not { } issued
            || issued.EventSettingsId != burn.Id)
            return null;

        return new ScannedTicket(
            issued.HolderName, issued.TicketLabel,
            IsVoid: !issued.IsValid, CheckedInAtVendor: false,
            GuestUserId: issued.HolderUserId, EntryUserId: issued.EntryUserId,
            TicketAttendeeId: null, CheckoutTicketId: issued.Id, VendorTicketId: null,
            InResaleQueue: issued.InResaleQueue);
    }

    private sealed record ScannedTicket(
        string? Name,
        string? TypeName,
        bool IsVoid,
        bool CheckedInAtVendor,
        Guid? GuestUserId,
        Guid? EntryUserId,
        Guid? TicketAttendeeId,
        Guid? CheckoutTicketId,
        string? VendorTicketId,
        bool InResaleQueue);

    public async Task<GateDecisionResult> RecordDecisionAsync(
        GateDecisionInput input, Guid scannedByUserId, CancellationToken ct = default)
    {
        if (!FitsStorage(GateBarcode.Normalize(input.Barcode), GateScanEvent.BarcodeMaxLength)
            || !FitsStorage(input.LaneId, GateScanEvent.LaneIdMaxLength)
            || !FitsStorage(input.Note, GateScanEvent.NoteMaxLength))
        {
            logger.LogWarning("Rejected gate decision exceeding storage limits from scanner {ScannerId}", scannedByUserId);
            return new GateDecisionResult(GateVerdict.Unresolved, null, null, false, null, null, InputTooLong: true);
        }

        var overrideAuthorized = input.ChildWithAdult || input.OverrideEarly;
        if (overrideAuthorized && VerifySharedOverridePin(input.SupervisorPin) is { } failure)
            return failure;

        // Re-evaluate server-side: a client-supplied "ID confirmed" can never turn a STOP into an admit.
        var eval = await EvaluateAsync(input.Barcode, ct);
        var verdict = ResolveVerdict(eval.Outcome, input, overrideAuthorized);
        var admit = IsAdmit(verdict);

        var scan = BuildEvent(eval, input, scannedByUserId, verdict, admit, overrideAuthorized);
        var recorded = await repository.RecordScanAsync(scan, ct);

        if (recorded == GateRecordOutcome.DuplicateAdmitRejected)
        {
            // Lost the concurrent race for this barcode's single admit slot.
            verdict = GateVerdict.RejectedDuplicate;
            await repository.RecordScanAsync(BuildEvent(eval, input, scannedByUserId, verdict, admit: false, overrideAuthorized), ct);
            var prior = await repository.GetAdmitForBarcodeAsync(eval.Barcode, ct);
            return new GateDecisionResult(verdict, eval.GuestName, eval.TicketTypeName,
                IsEarly: false, prior?.OccurredAt, prior?.ScannedByUserId);
        }

        if (admit)
        {
            await MarkAttendedAsync(eval.GuestUserId);
            if (eval.VendorTicketId is { Length: > 0 } vendorTicketId)
                MirrorVendorCheckIn(vendorTicketId, scan.OccurredAt);
        }

        return new GateDecisionResult(verdict, eval.GuestName, eval.TicketTypeName,
            IsEarly: admit && eval.IsEarly,
            eval.PreviousAdmitAt, eval.PreviousAdmitByUserId,
            VendorTicketId: admit ? eval.VendorTicketId : null);
    }

    private static bool FitsStorage(string? value, int limit) =>
        value is null || value.EnumerateRunes().Count() <= limit;

    private GateDecisionResult? VerifySharedOverridePin(string? pin)
    {
        var configured = configuration[SupervisorPinConfigKey];
        if (string.IsNullOrEmpty(configured))
            return Refused(GateOverrideFailure.NotConfigured);
        if (pinThrottle.SecondsUntilRetry(SharedOverridePinKey) is { } wait)
            return Refused(GateOverrideFailure.LockedOut, wait);

        // Fixed-width hashes keep both equality and PIN length out of the timing signal.
        if (string.IsNullOrEmpty(pin) || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(pin)), SHA256.HashData(Encoding.UTF8.GetBytes(configured))))
        {
            pinThrottle.RecordFailure(SharedOverridePinKey);
            return Refused(GateOverrideFailure.InvalidPin);
        }

        pinThrottle.Reset(SharedOverridePinKey);
        return null;
    }

    private static GateDecisionResult Refused(GateOverrideFailure failure, int? retryAfterSeconds = null) =>
        new(GateVerdict.Unresolved, null, null, false, null, null,
            OverrideFailure: failure, RetryAfterSeconds: retryAfterSeconds);

    /// <summary>
    /// Fire-and-forget. Marked in the ledger so the backfill page never re-sends a live admit, but only while the
    /// flag is on: with it off the job no-ops, and a mark would hide the rows the backfill exists to recover.
    /// </summary>
    private void MirrorVendorCheckIn(string vendorTicketId, Instant admittedAt)
    {
        try
        {
            var admittedAtUnixSeconds = admittedAt.ToUnixTimeSeconds();
            backgroundJobs.Enqueue<GateVendorCheckInJob>(j => j.ExecuteAsync(vendorTicketId, admittedAtUnixSeconds, CancellationToken.None));
            if (configuration.GetValue<bool>(VendorMirrorEnabledKey))
                mirrorLedger.TryMarkSent(vendorTicketId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Vendor check-in mirror could not be enqueued for admitted ticket {VendorTicketId}", vendorTicketId);
        }
    }

    /// <summary>
    /// Without this, consumed-Early-Entry guards might never learn of a gate check-in: the vendor mirror is best-effort
    /// and off by default. Skipped when the guest is unmatched or no event is active.
    /// </summary>
    private async Task MarkAttendedAsync(Guid? guestUserId)
    {
        if (guestUserId is null)
            return;

        // The admit is already durable: ignore the request token, and never fail the response. A unique-index
        // collision with the ticket sync writing the same fact is benign; the next sync converges the row.
        try
        {
            var activeEvent = settingsService.GetActiveEventSettings();
            if (activeEvent is null || activeEvent.Year == 0)
                return;

            await ticketWrite.RecordParticipationAsync(
                guestUserId.Value, activeEvent.Year, ParticipationStatus.Attended,
                clock.GetCurrentInstant(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Attendance projection failed for admitted guest {GuestUserId}; participation row not marked Attended", guestUserId);
        }
    }

    public async Task<GateLeaderboard> GetLeaderboardAsync(Instant since, CancellationToken ct = default)
    {
        var scans = await repository.GetScansSinceAsync(since, ct);
        var rows = scans
            .GroupBy(s => s.ScannedByUserId)
            .Select(g =>
            {
                var admitted = g.Count(s => s.IsLiveAdmit);
                return new GateLeaderboardRow(g.Key, admitted, g.Count() - admitted, g.Count());
            })
            .ToList();
        return new GateLeaderboard(rows.Sum(r => r.Admitted), scans.Count, rows);
    }

    public async Task<IReadOnlyList<GateNameMatch>> FindTicketsByNameAsync(string? query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        if (q.Length < MinSearchLength)
            return [];

        // Legal names are searched on purpose (G3): the guest says a name, the agent then checks it against the photo ID.
        // The card shows only the ticket's own name, never the burner name or the legal name it matched on.
        var people = (await users.SearchUsersAsync(q, PersonSearchFields.Name | PersonSearchFields.LegalName, 50, ct))
            .Select(m => m.UserId)
            .ToHashSet();
        bool Matches(string? ticketName, params Guid?[] userIds) =>
            NameContains(ticketName, q) || userIds.Any(id => id is { } uid && people.Contains(uid));

        var vendor = (await tickets.GetTicketOrdersAsync(ct))
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .Where(a => a.Status != TicketAttendeeStatus.Void && !string.IsNullOrWhiteSpace(a.Barcode)
                && Matches(a.AttendeeName, a.MatchedUserId))
            .Select(a => new GateNameMatch(GateBarcode.Normalize(a.Barcode), a.AttendeeName ?? string.Empty, a.TicketTypeName));

        var burn = settingsService.GetActiveEventSettings();
        var issued = burn is null
            ? []
            : (await issuedTickets.GetIssuedTicketsForEventAsync(burn.Id, ct))
                .Where(t => (t.IsValid || t.InResaleQueue) && Matches(t.HolderName, t.HolderUserId, t.EntryUserId))
                .Select(t => new GateNameMatch(GateBarcode.Normalize(t.Barcode), t.HolderName, t.TicketLabel));

        return vendor.Concat(issued)
            .DistinctBy(m => m.Barcode, StringComparer.Ordinal)
            .Take(MaxSearchResults)
            .ToList();
    }

    private const int MinSearchLength = 2;
    private const int MaxSearchResults = 20;

    private static bool NameContains(string? name, string query) =>
        !string.IsNullOrEmpty(name)
        && CultureInfo.InvariantCulture.CompareInfo.IndexOf(name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    public async Task<GateUndoResult> UndoAdmitAtGateAsync(
        string barcode, string? supervisorPin, Guid actorUserId, CancellationToken ct = default)
    {
        if (VerifySharedOverridePin(supervisorPin) is { OverrideFailure: { } failure } refused)
            return new GateUndoResult(GateUndoOutcome.PinRefused, PinFailure: failure, RetryAfterSeconds: refused.RetryAfterSeconds);

        var code = GateBarcode.Normalize(barcode);
        if (code.Length == 0 || await repository.GetAdmitForBarcodeAsync(code, ct) is not { } admit)
            return new GateUndoResult(GateUndoOutcome.NotAdmitted);

        return await UndoAsync(admit, UndoneAtGateReason, actorUserId, ct);
    }

    public async Task<GateUndoResult> UndoAdmitAsync(Guid scanId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var why = reason?.Trim() ?? string.Empty;
        if (why.Length == 0 || !FitsStorage(why, GateScanEvent.NoteMaxLength))
            return new GateUndoResult(GateUndoOutcome.ReasonRequired);

        var admit = (await repository.GetScansSinceAsync(Instant.MinValue, ct)).FirstOrDefault(s => s.Id == scanId && s.IsLiveAdmit);
        if (admit is null)
            return new GateUndoResult(GateUndoOutcome.NotAdmitted);
        return await UndoAsync(admit, why, actorUserId, ct);
    }

    internal const string UndoneAtGateReason = "Undone at the gate with the supervisor PIN";

    /// <summary>Wider than the arrival window of one event, narrower than the gap to the last one.</summary>
    private static readonly Duration SameEventWindow = Duration.FromDays(30);

    private async Task<GateUndoResult> UndoAsync(GateScanEvent admit, string reason, Guid actorUserId, CancellationToken ct)
    {
        if (await IsAtVendorAsync(admit, ct))
            return new GateUndoResult(GateUndoOutcome.VendorCheckedIn);

        var undone = await repository.UndoAdmitAsync(admit.Id, clock.GetCurrentInstant(), actorUserId, reason, ct);
        if (undone is null)
            return new GateUndoResult(GateUndoOutcome.NotAdmitted);

        await auditLog.LogAsync(GateAuditActions.GateAdmitUndone, nameof(GateScanEvent), undone.Id,
            $"Undid a gate admit ({undone.Verdict}, {undone.OccurredAt.ToIso8601()}): {reason}", actorUserId,
            relatedEntityId: undone.GuestUserId, relatedEntityType: undone.GuestUserId is null ? null : AuditSubjectTypes.User);

        // The undo is committed: finish the rollback even if the kiosk disconnects, since a retry finds nothing to undo.
        if (undone.GuestUserId is { } guest)
            await ClearAttendedAsync(guest, undone.OccurredAt, CancellationToken.None);

        return new GateUndoResult(GateUndoOutcome.Undone);
    }

    /// <summary>
    /// With the mirror on, the admit's check-in job may post after the undo. Either way the ticket sync would read the
    /// vendor's check-in back and mark the guest Attended again, and the gate would STOP the rescan as a duplicate.
    /// </summary>
    private async Task<bool> IsAtVendorAsync(GateScanEvent admit, CancellationToken ct)
    {
        if (admit.TicketAttendeeId is not { } attendeeId)
            return false;
        if (configuration.GetValue<bool>(VendorMirrorEnabledKey))
            return true;

        var attendee = (await tickets.GetTicketOrdersAsync(ct))
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => a.Id == attendeeId);
        return attendee is not null && (attendee.CheckedInAt is not null || attendee.Status == TicketAttendeeStatus.CheckedIn);
    }

    /// <summary>Only when no other admit of theirs stands, or a second ticket of theirs would read as not arrived.</summary>
    private async Task ClearAttendedAsync(Guid guestUserId, Instant admittedAt, CancellationToken ct)
    {
        var activeEvent = settingsService.GetActiveEventSettings();
        if (activeEvent is null || activeEvent.Year == 0)
            return;

        var others = await repository.GetScansForUserAsync(guestUserId, ct);
        if (others.Any(s => s.GuestUserId == guestUserId && s.IsLiveAdmit && s.OccurredAt >= admittedAt - SameEventWindow))
            return;

        await ticketWrite.UndoCheckInAsync(guestUserId, activeEvent.Year, ct);
    }

    public async Task<GateArrivalsReport?> GetArrivalsReportAsync(CancellationToken ct = default)
    {
        var burn = settingsService.GetActiveEventSettings();
        if (burn is null)
            return null;
        var zone = EventZone(burn.TimeZoneId);

        var attendees = (await tickets.GetTicketOrdersAsync(ct))
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .ToList();
        var issued = await issuedTickets.GetIssuedTicketsForEventAsync(burn.Id, ct);
        var vendorById = attendees.ToDictionary(a => a.Id);
        var issuedById = issued.ToDictionary(t => t.Id);

        // Scans carry no event: a scan belongs to this event when its ticket does. Unknown barcodes stay out.
        var scans = (await repository.GetScansSinceAsync(Instant.MinValue, ct))
            .Select(s => (Scan: s, Ticket: TicketOf(s)))
            .Where(x => x.Ticket is not null)
            .ToList();

        var arrivals = scans
            .Where(x => IsAdmit(x.Scan.Verdict))
            .Select(x => new GateArrival(
                x.Scan.Id, x.Scan.OccurredAt, x.Scan.OccurredAt.InZone(zone).Date,
                x.Ticket!.Value.Name, x.Ticket.Value.Label, x.Scan.Verdict, x.Scan.GuestUserId,
                x.Scan.UndoneAt, x.Scan.UndoneByUserId, x.Scan.UndoReason))
            .ToList();

        var refusedByDay = scans
            .Where(x => IsRefusal(x.Scan.Verdict))
            .GroupBy(x => x.Scan.OccurredAt.InZone(zone).Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var days = arrivals
            .Select(a => a.Day)
            .Concat(refusedByDay.Keys)
            .Distinct()
            .Select(day =>
            {
                var live = arrivals.Where(a => a.Day == day && a.UndoneAt is null).ToList();
                return new GateArrivalDay(
                    day,
                    Admitted: live.Count,
                    Early: live.Count(a => a.Verdict == GateVerdict.AdmittedEarly),
                    Overrides: live.Count(a => a.Verdict is GateVerdict.AdmittedEarlyOverride or GateVerdict.AdmittedChildWithAdult),
                    Refused: refusedByDay.GetValueOrDefault(day),
                    Undone: arrivals.Count(a => a.Day == day && a.UndoneAt is not null));
            })
            .ToList();

        var held = attendees.Count(a => a.Status != TicketAttendeeStatus.Void)
            + issued.Count(t => t.IsValid || t.InResaleQueue);

        return new GateArrivalsReport(burn.EventName, zone, clock.GetCurrentInstant().InZone(zone).Date, held, days, arrivals);

        (string Name, string? Label)? TicketOf(GateScanEvent s) =>
            s.TicketAttendeeId is { } aid && vendorById.TryGetValue(aid, out var a) ? (a.AttendeeName ?? string.Empty, a.TicketTypeName)
            : s.CheckoutTicketId is { } tid && issuedById.TryGetValue(tid, out var t) ? (t.HolderName, t.TicketLabel)
            : null;
    }

    private static bool IsRefusal(GateVerdict v) =>
        v is GateVerdict.RejectedInvalid or GateVerdict.RejectedDuplicate
            or GateVerdict.RejectedTooEarly or GateVerdict.RejectedNameMismatch;

    public async Task<GateSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var s = await repository.GetSettingsAsync(ct);
        return new GateSettingsDto(s.GeneralEntryOpensAt, s.MinorAgeThresholdYears, s.IsCutoffConfigured);
    }

    public Task SaveSettingsAsync(GateSettingsDto settings, CancellationToken ct = default) =>
        repository.SaveSettingsAsync(
            new GateSettings
            {
                Id = 1,
                GeneralEntryOpensAt = settings.GeneralEntryOpensAt,
                MinorAgeThresholdYears = settings.MinorAgeThresholdYears,
            },
            ct);

    public Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default) =>
        repository.PurgeScansBeforeAsync(cutoff, ct);

    /// <summary>Covers the whole event and its Early Entry run-up while keeping prior editions' admits out of the diff.</summary>
    private static readonly Duration VendorBackfillWindow = Duration.FromDays(30);

    public async Task<GateVendorBackfillSnapshot> GetVendorCheckInBackfillAsync(CancellationToken ct = default)
    {
        var scans = await repository.GetScansSinceAsync(clock.GetCurrentInstant().Minus(VendorBackfillWindow), ct);
        // Checkout-issued tickets live only in Humans: there is no vendor to mirror them to.
        var admits = scans.Where(s => s.IsLiveAdmit && s.CheckoutTicketId is null).ToList();

        var orders = await tickets.GetTicketOrdersAsync(ct);
        var attendees = orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .ToDictionary(a => a.Id);

        List<GateVendorBackfillRow> pending = [];
        List<GateVendorBackfillRow> unmirrorable = [];
        List<GateVendorBackfillRow> sent = [];
        var alreadyCheckedIn = 0;

        foreach (var admit in admits)
        {
            var attendee = admit.TicketAttendeeId is { } id ? attendees.GetValueOrDefault(id) : null;
            if (attendee is null || string.IsNullOrEmpty(attendee.VendorTicketId))
                unmirrorable.Add(new GateVendorBackfillRow(
                    admit.Barcode, attendee?.AttendeeName, admit.OccurredAt, VendorTicketId: null));
            // A checked-in ticket keeps Status Valid, so testing Status alone would re-post every synced check-in.
            else if (attendee.CheckedInAt is not null || attendee.Status == TicketAttendeeStatus.CheckedIn)
                alreadyCheckedIn++;
            else
            {
                var row = new GateVendorBackfillRow(admit.Barcode, attendee.AttendeeName, admit.OccurredAt, attendee.VendorTicketId);
                (mirrorLedger.WasSent(attendee.VendorTicketId) ? sent : pending).Add(row);
            }
        }

        return new GateVendorBackfillSnapshot(admits.Count, alreadyCheckedIn, pending, unmirrorable, sent,
            configuration.GetValue<bool>(VendorMirrorEnabledKey));
    }

    public async Task<GateVendorBackfillResult> EnqueueVendorCheckInBackfillAsync(
        string? vendorTicketId, CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>(VendorMirrorEnabledKey))
            return new GateVendorBackfillResult(false, []);

        var snapshot = await GetVendorCheckInBackfillAsync(ct);
        List<GateVendorBackfillRow> enqueued = [];
        foreach (var row in snapshot.Pending)
        {
            if (vendorTicketId is not null && !string.Equals(row.VendorTicketId, vendorTicketId, StringComparison.Ordinal))
                continue;
            // Claim before enqueue: overlapping backfill requests must not double-post a check-in.
            if (!mirrorLedger.TryMarkSent(row.VendorTicketId!))
                continue;

            var admittedAtUnixSeconds = row.AdmittedAt.ToUnixTimeSeconds();
            // The job keeps the original admit time and runs independently of the admin request.
            backgroundJobs.Enqueue<GateVendorCheckInJob>(j =>
                j.ExecuteAsync(row.VendorTicketId!, admittedAtUnixSeconds, CancellationToken.None));
            enqueued.Add(row);
        }

        return new GateVendorBackfillResult(true, enqueued);
    }

    public async Task<IReadOnlyList<Guid>> GetShiftRosterAsync(
        Guid rosterTeamId, CancellationToken ct = default)
    {
        var activeEvent = settingsService.GetActiveEventSettings();
        if (activeEvent is null)
            return [];

        var now = clock.GetCurrentInstant();

        var gateShifts = await shifts.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            activeEvent.Id, DepartmentId: rosterTeamId, Flags: ShiftBrowseQueryFlags.IncludeSignups), ct);

        return gateShifts
            .Where(s => s.AbsoluteStart >= now - RosterWindow && s.AbsoluteStart <= now + RosterWindow)
            .SelectMany(s => s.Signups)
            .Where(u => u.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .DistinctBy(u => u.UserId)
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => u.UserId)
            .ToList();
    }

    public async Task<GatePinStatus> GetPinStatusAsync(Guid userId, CancellationToken ct = default) =>
        new(await repository.GetStaffPinAsync(userId, ct) is not null, await IsSupervisorAsync(userId, ct));

    public async Task<GatePinSetResult> SetOwnPinAsync(Guid userId, string pin, CancellationToken ct = default)
    {
        if (!IsValidPin(pin))
            return GatePinSetResult.InvalidPin;
        // AdminEnrolled false: AuthorizeOverrideAsync (uncalled outside tests, dormant with the claim flow,
        // nobodies-collective/Humans#933) rejects a self-enrolled PIN.
        await StorePinAsync(userId, pin, adminEnrolled: false, ct);
        // Actor is the claimed staffer; the PIN value is never logged.
        await auditLog.LogAsync(GateAuditActions.GateStaffPinSet, nameof(GateStaffPin), userId,
            "Staffer self-enrolled their gate claim PIN at the kiosk", userId);
        return GatePinSetResult.Ok;
    }

    public async Task<bool> AdminSetPinAsync(Guid userId, string pin, Guid actorUserId, CancellationToken ct = default)
    {
        if (!IsValidPin(pin))
            return false;
        // The only PIN AuthorizeOverrideAsync accepts (uncalled outside tests; see SetOwnPinAsync).
        await StorePinAsync(userId, pin, adminEnrolled: true, ct);
        await auditLog.LogAsync(GateAuditActions.GateStaffPinSet, nameof(GateStaffPin), userId,
            "Admin set a staffer's gate PIN (override-enrolled)", actorUserId);
        return true;
    }

    public async Task<bool> VerifyPinAsync(Guid userId, string pin, CancellationToken ct = default)
    {
        var row = await repository.GetStaffPinAsync(userId, ct);
        if (row is null)
            return false;
        return pinHasher.VerifyHashedPassword(row, row.PinHash, pin) != PasswordVerificationResult.Failed;
    }

    public async Task<bool> AuthorizeOverrideAsync(Guid supervisorUserId, string pin, CancellationToken ct = default)
    {
        // Verify only, never enrol. Needs an admin-enrolled PIN, the right PIN and a currently held supervisor role.
        var row = await repository.GetStaffPinAsync(supervisorUserId, ct);
        if (row is not { AdminEnrolled: true })
            return false;
        if (pinHasher.VerifyHashedPassword(row, row.PinHash, pin) == PasswordVerificationResult.Failed)
            return false;
        return await IsSupervisorAsync(supervisorUserId, ct);
    }

    public async Task ClearPinAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        await repository.DeleteStaffPinAsync(userId, ct);
        await auditLog.LogAsync(GateAuditActions.GateStaffPinReset, nameof(GateStaffPin), userId,
            "Admin reset a staffer's gate PIN", actorUserId);
    }

    private async Task<bool> IsSupervisorAsync(Guid userId, CancellationToken ct)
    {
        foreach (var role in SupervisorRoles)
            if (await roles.HasActiveRoleAsync(userId, role, ct))
                return true;
        return false;
    }

    private async Task StorePinAsync(Guid userId, string pin, bool adminEnrolled, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var entity = new GateStaffPin { UserId = userId, CreatedAt = now, UpdatedAt = now, AdminEnrolled = adminEnrolled };
        entity.PinHash = pinHasher.HashPassword(entity, pin);
        await repository.UpsertStaffPinAsync(entity, ct);
    }

    /// <summary>Exactly four digits and not a trivial run or repeat (0000, 1234, 4321…).</summary>
    private static bool IsValidPin(string? pin)
    {
        if (pin is null || pin.Length != 4 || !pin.All(char.IsAsciiDigit))
            return false;
        if (pin.Distinct().Count() == 1)
            return false;
        return !"0123456789".Contains(pin, StringComparison.Ordinal)
            && !"9876543210".Contains(pin, StringComparison.Ordinal);
    }

    // actorUserId/now are unused: scan rows carry no audit column, and AccountMergeService audits the merge.
    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignUserAsync(mergedFromUserId, mergedToUserId, ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var scans = await repository.GetScansForUserAsync(userId, ct);

        // Data-minimized: the member's own gate activity, without the barcode or anyone else's identifiers.
        var slice = new UserDataSlice(GateScans, scans.Select(s => new
        {
            OccurredAt = s.OccurredAt.ToIso8601(),
            Verdict = s.Verdict.ToString(),
            Role = s.GuestUserId == userId ? "Guest" : s.ScannedByUserId == userId ? "Scanner" : "UndidAdmit",
            s.LaneId,
            UndoneAt = s.UndoneAt.ToIso8601(),
        }).ToList());

        return [slice];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GateScans] =
                "Partially retained: scans the person performed or undid as gate staff keep their " +
                "attribution — ScannedByUserId and UndoneByUserId are operational columns and the row " +
                "is the venue's access-control record (GDPR Art. 17(3)(b) and (e)). Their own " +
                "admission links (GuestUserId, OverrideByUserId) and their gate staff PIN are cleared."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.EraseUserFromScansAsync(userId, ct);

    private static GateScanResult NotFound(string code) =>
        new(GatePreCheckOutcome.Invalid, code, null, null, false, null, null, null, null, null);

    private static GateVerdict ResolveVerdict(GatePreCheckOutcome outcome, GateDecisionInput input, bool overrideAuthorized) => outcome switch
    {
        GatePreCheckOutcome.Invalid => GateVerdict.RejectedInvalid,
        GatePreCheckOutcome.Duplicate => GateVerdict.RejectedDuplicate,
        GatePreCheckOutcome.CutoffNotConfigured => GateVerdict.Unresolved,
        GatePreCheckOutcome.EarlyEntryUnknown =>
            overrideAuthorized ? GateVerdict.AdmittedEarlyOverride : GateVerdict.Unresolved,
        GatePreCheckOutcome.TooEarly =>
            overrideAuthorized ? GateVerdict.AdmittedEarlyOverride : GateVerdict.RejectedTooEarly,
        GatePreCheckOutcome.NeedsIdCheck or GatePreCheckOutcome.NeedsIdCheckEarly =>
            // The child-without-ID waiver is itself an override.
            input.ChildWithAdult
                ? (overrideAuthorized ? GateVerdict.AdmittedChildWithAdult : GateVerdict.RejectedNameMismatch)
            : input.IdConfirmed
                ? (outcome == GatePreCheckOutcome.NeedsIdCheckEarly ? GateVerdict.AdmittedEarly : GateVerdict.Admitted)
                : GateVerdict.RejectedNameMismatch,
        _ => GateVerdict.Unresolved,
    };

    private GateScanEvent BuildEvent(
        GateScanResult eval, GateDecisionInput input, Guid scannedByUserId, GateVerdict verdict, bool admit, bool overrideAuthorized) =>
        new()
        {
            Id = Guid.NewGuid(),
            OccurredAt = clock.GetCurrentInstant(),
            ScannedByUserId = scannedByUserId,
            Barcode = eval.Barcode,
            TicketAttendeeId = eval.TicketAttendeeId,
            CheckoutTicketId = eval.CheckoutTicketId,
            GuestUserId = eval.GuestUserId,
            Verdict = verdict,
            LaneId = input.LaneId,
            ClientScanAt = input.ClientScanAt,
            Note = input.Note,
            OverrideByUserId = admit && overrideAuthorized ? scannedByUserId : null,
            AdmitDedupeKey = admit ? eval.Barcode : null,
        };

    private static bool IsAdmit(GateVerdict v) =>
        v is GateVerdict.Admitted or GateVerdict.AdmittedEarly
            or GateVerdict.AdmittedChildWithAdult or GateVerdict.AdmittedEarlyOverride;

    private static DateTimeZone EventZone(string? timeZoneId) =>
        (timeZoneId is not null ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) : null) ?? DateTimeZone.Utc;
}
