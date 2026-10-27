using Humans.Auth.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Constants;
using Humans.Gate.Contracts;
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
    IPasswordHasher<GateStaffPin> pinHasher,
    IAuditLogService auditLog,
    IBackgroundJobClient backgroundJobs,
    GateVendorMirrorLedger mirrorLedger,
    IConfiguration configuration,
    ILogger<GateService> logger,
    IClock clock) : IGateService, IGateScanRetention, IUserMerge, IUserDataContributor
{
    internal const string GateScans = "GateScans";

    private const string VendorMirrorEnabledKey = "Gate:VendorMirrorEnabled";

    /// <summary>How far either side of now a gate shift may start to count as current.</summary>
    private static readonly Duration RosterWindow = Duration.FromHours(2);

    /// <summary>Server-verified, never client-asserted.</summary>
    private static readonly string[] SupervisorRoles = [RoleNames.Admin, RoleNames.Board, RoleNames.TicketAdmin];

    public async Task<GateScanResult> EvaluateAsync(string barcode, CancellationToken ct = default)
    {
        var code = GateBarcode.Normalize(barcode);
        if (code.Length == 0)
            return NotFound(code);

        var orders = await tickets.GetTicketOrdersAsync(ct);
        var attendee = orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => a.Barcode is not null
                && string.Equals(GateBarcode.Normalize(a.Barcode), code, StringComparison.Ordinal));

        if (attendee is null)
            return NotFound(code);

        var settings = await repository.GetSettingsAsync(ct);
        var priorAdmit = await repository.GetAdmitForBarcodeAsync(code, ct);
        var burn = await settingsService.GetActiveEventSettingsAsync(ct);
        var now = clock.GetCurrentInstant();
        var zone = EventZone(burn?.TimeZoneId);
        var today = now.InZone(zone).Date;

        EarlyEntryRosterRow? ee = attendee.MatchedUserId is { } uid
            ? await earlyEntry.GetForUserAsync(uid, ct)
            : null;

        var outcome = GateAdmissionRules.Evaluate(new GateScanContext(
            Found: true,
            IsVoid: attendee.Status == TicketAttendeeStatus.Void,
            AlreadyAdmittedLocally: priorAdmit is not null,
            CheckedInAtVendor: attendee.CheckedInAt is not null
                || attendee.Status == TicketAttendeeStatus.CheckedIn,
            Now: now,
            GeneralEntryOpensAt: settings.IsCutoffConfigured ? settings.GeneralEntryOpensAt : null,
            MatchedToHuman: attendee.MatchedUserId is not null,
            EarliestEntryDate: ee?.EarliestEntryDate,
            Today: today));

        return new GateScanResult(
            outcome,
            code,
            attendee.AttendeeName,
            attendee.TicketTypeName,
            IsEarly: outcome == GatePreCheckOutcome.NeedsIdCheckEarly,
            EarlyEntrySource: ee is { Sources.Count: > 0 } ? string.Join(", ", ee.Sources) : null,
            TicketAttendeeId: attendee.Id,
            GuestUserId: attendee.MatchedUserId,
            PreviousAdmitAt: priorAdmit?.OccurredAt,
            PreviousAdmitByUserId: priorAdmit?.ScannedByUserId,
            VendorTicketId: attendee.VendorTicketId,
            // Date only, never the EE source (privacy).
            EarliestEntryDate: ee?.EarliestEntryDate,
            Today: today,
            GeneralEntryDate: settings.IsCutoffConfigured ? settings.GeneralEntryOpensAt.InZone(zone).Date : null);
    }

    public async Task<GateDecisionResult> RecordDecisionAsync(
        GateDecisionInput input, Guid scannedByUserId, CancellationToken ct = default)
    {
        // Re-evaluate server-side: a client-supplied "ID confirmed" can never turn a STOP into an admit.
        var eval = await EvaluateAsync(input.Barcode, ct);
        var verdict = ResolveVerdict(eval.Outcome, input);
        var admit = IsAdmit(verdict);

        var recorded = await repository.RecordScanAsync(BuildEvent(eval, input, scannedByUserId, verdict, admit), ct);

        if (recorded == GateRecordOutcome.DuplicateAdmitRejected)
        {
            // Lost the concurrent race for this barcode's single admit slot.
            verdict = GateVerdict.RejectedDuplicate;
            await repository.RecordScanAsync(BuildEvent(eval, input, scannedByUserId, verdict, admit: false), ct);
            var prior = await repository.GetAdmitForBarcodeAsync(eval.Barcode, ct);
            return new GateDecisionResult(verdict, eval.GuestName, eval.TicketTypeName,
                IsEarly: false, prior?.OccurredAt, prior?.ScannedByUserId);
        }

        if (admit)
        {
            await MarkAttendedAsync(eval.GuestUserId);
            if (eval.VendorTicketId is { Length: > 0 } vendorTicketId)
                MirrorVendorCheckIn(vendorTicketId);
        }

        return new GateDecisionResult(verdict, eval.GuestName, eval.TicketTypeName,
            IsEarly: admit && eval.IsEarly,
            eval.PreviousAdmitAt, eval.PreviousAdmitByUserId,
            VendorTicketId: admit ? eval.VendorTicketId : null);
    }

    /// <summary>
    /// Fire-and-forget. Marked in the ledger so the backfill page never re-sends a live admit, but only while the
    /// flag is on: with it off the job no-ops, and a mark would hide the rows the backfill exists to recover.
    /// </summary>
    private void MirrorVendorCheckIn(string vendorTicketId)
    {
        try
        {
            backgroundJobs.Enqueue<GateVendorCheckInJob>(j => j.ExecuteAsync(vendorTicketId, CancellationToken.None));
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
            var activeEvent = await settingsService.GetActiveEventSettingsAsync();
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
                var admitted = g.Count(s => IsAdmit(s.Verdict));
                return new GateLeaderboardRow(g.Key, admitted, g.Count() - admitted, g.Count());
            })
            .ToList();
        return new GateLeaderboard(rows.Sum(r => r.Admitted), scans.Count, rows);
    }

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
        var admits = scans.Where(s => IsAdmit(s.Verdict)).ToList();

        var orders = await tickets.GetTicketOrdersAsync(ct);
        var attendees = orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .ToDictionary(a => a.Id);

        List<GateVendorBackfillRow> pending = [];
        List<GateVendorBackfillRow> unmirrorable = [];
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
                pending.Add(new GateVendorBackfillRow(
                    admit.Barcode, attendee.AttendeeName, admit.OccurredAt, attendee.VendorTicketId));
        }

        return new GateVendorBackfillSnapshot(admits.Count, alreadyCheckedIn, pending, unmirrorable);
    }

    public async Task<IReadOnlyList<GateRosterEntry>> GetShiftRosterAsync(
        Guid rosterTeamId, CancellationToken ct = default)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        if (activeEvent is null)
            return [];

        // Shift dates are DayOffsets from GateOpeningDate: resolve each start in the event zone and keep those near now.
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        var now = clock.GetCurrentInstant();
        var openingDate = activeEvent.GateOpeningDate;

        var gateShifts = await shifts.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            activeEvent.Id, DepartmentId: rosterTeamId, Flags: ShiftBrowseQueryFlags.IncludeSignups));

        return gateShifts
            .Where(s => StartsWithinWindow(openingDate, s.Shift, zone, now))
            .SelectMany(s => s.Signups)
            .Where(u => u.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .DistinctBy(u => u.UserId)
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new GateRosterEntry(u.UserId, u.DisplayName))
            .ToList();
    }

    private static bool StartsWithinWindow(LocalDate openingDate, ShiftInfo shift, DateTimeZone zone, Instant now)
    {
        var start = openingDate.PlusDays(shift.DayOffset).At(shift.StartTime)
            .InZoneLeniently(zone).ToInstant();
        return start >= now - RosterWindow && start <= now + RosterWindow;
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
        await auditLog.LogAsync(AuditAction.GateStaffPinSet, nameof(GateStaffPin), userId,
            "Staffer self-enrolled their gate claim PIN at the kiosk", userId);
        return GatePinSetResult.Ok;
    }

    public async Task<bool> AdminSetPinAsync(Guid userId, string pin, Guid actorUserId, CancellationToken ct = default)
    {
        if (!IsValidPin(pin))
            return false;
        // The only PIN AuthorizeOverrideAsync accepts (uncalled outside tests; see SetOwnPinAsync).
        await StorePinAsync(userId, pin, adminEnrolled: true, ct);
        await auditLog.LogAsync(AuditAction.GateStaffPinSet, nameof(GateStaffPin), userId,
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
        await auditLog.LogAsync(AuditAction.GateStaffPinReset, nameof(GateStaffPin), userId,
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
            Role = s.GuestUserId == userId ? "Guest" : "Scanner",
            s.LaneId,
        }).ToList());

        return [slice];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GateScans] =
                "Partially retained: scans the person performed as gate staff keep their " +
                "attribution — ScannedByUserId is a non-nullable operational column and the row " +
                "is the venue's access-control record (GDPR Art. 17(3)(b) and (e)). Their own " +
                "admission links (GuestUserId, OverrideByUserId) and their gate staff PIN are cleared."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.EraseUserFromScansAsync(userId, ct);

    private static GateScanResult NotFound(string code) =>
        new(GatePreCheckOutcome.Invalid, code, null, null, false, null, null, null, null, null);

    private static GateVerdict ResolveVerdict(GatePreCheckOutcome outcome, GateDecisionInput input) => outcome switch
    {
        GatePreCheckOutcome.Invalid => GateVerdict.RejectedInvalid,
        GatePreCheckOutcome.Duplicate => GateVerdict.RejectedDuplicate,
        GatePreCheckOutcome.CutoffNotConfigured => GateVerdict.Unresolved,
        // The controller sets OverrideByUserId only after verifying the supervisor PIN, so non-null is proof.
        GatePreCheckOutcome.EarlyEntryUnknown =>
            input.OverrideByUserId is not null ? GateVerdict.AdmittedEarlyOverride : GateVerdict.Unresolved,
        GatePreCheckOutcome.TooEarly =>
            input.OverrideByUserId is not null ? GateVerdict.AdmittedEarlyOverride : GateVerdict.RejectedTooEarly,
        GatePreCheckOutcome.NeedsIdCheck or GatePreCheckOutcome.NeedsIdCheckEarly =>
            // The child-without-ID waiver is itself an override.
            input.ChildWithAdult
                ? (input.OverrideByUserId is not null ? GateVerdict.AdmittedChildWithAdult : GateVerdict.RejectedNameMismatch)
            : input.IdConfirmed
                ? (outcome == GatePreCheckOutcome.NeedsIdCheckEarly ? GateVerdict.AdmittedEarly : GateVerdict.Admitted)
                : GateVerdict.RejectedNameMismatch,
        _ => GateVerdict.Unresolved,
    };

    private GateScanEvent BuildEvent(
        GateScanResult eval, GateDecisionInput input, Guid scannedByUserId, GateVerdict verdict, bool admit) =>
        new()
        {
            Id = Guid.NewGuid(),
            OccurredAt = clock.GetCurrentInstant(),
            ScannedByUserId = scannedByUserId,
            Barcode = eval.Barcode,
            TicketAttendeeId = eval.TicketAttendeeId,
            GuestUserId = eval.GuestUserId,
            Verdict = verdict,
            LaneId = input.LaneId,
            ClientScanAt = input.ClientScanAt,
            Note = input.Note,
            OverrideByUserId = admit ? input.OverrideByUserId : null,
            AdmitDedupeKey = admit ? eval.Barcode : null,
        };

    private static bool IsAdmit(GateVerdict v) =>
        v is GateVerdict.Admitted or GateVerdict.AdmittedEarly
            or GateVerdict.AdmittedChildWithAdult or GateVerdict.AdmittedEarlyOverride;

    private static DateTimeZone EventZone(string? timeZoneId) =>
        (timeZoneId is not null ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) : null) ?? DateTimeZone.Utc;
}
