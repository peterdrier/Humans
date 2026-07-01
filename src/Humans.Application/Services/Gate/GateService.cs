using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Humans.Application.Services.Gate;

/// <summary>
/// The Gate (admissions) section service. Evaluates scans against
/// <see cref="GateAdmissionRules"/> using cross-section reads (Tickets,
/// EarlyEntry, BurnSettings), records the agent's decision as an append-only
/// <c>gate_scan_events</c> row through <see cref="IGateRepository"/>, and reads
/// back leaderboard/settings. The cutoff is always evaluated against the server
/// clock (<see cref="IClock"/>), never a device clock.
/// </summary>
public sealed class GateService(
    IGateRepository repository,
    ITicketServiceRead tickets,
    IEarlyEntryService earlyEntry,
    IBurnSettingsService burnSettings,
    IShiftManagementService shifts,
    IRoleAssignmentService roles,
    IPasswordHasher<GateStaffPin> pinHasher,
    IAuditLogService auditLog,
    IClock clock) : IGateService, IUserMerge, IUserDataContributor
{
    /// <summary>How far either side of "now" a gate shift may start to count as current.</summary>
    private static readonly Duration RosterWindow = Duration.FromHours(2);

    /// <summary>Roles whose holders may authorize a gate supervisor override (server-verified, never client-asserted).</summary>
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
        var burn = await burnSettings.GetActiveAsync(ct);
        var now = clock.GetCurrentInstant();
        var zone = EventZone(burn?.TimeZoneId);
        var today = now.InZone(zone).Date;

        UserEarlyEntry? ee = attendee.MatchedUserId is { } uid
            ? await earlyEntry.GetForUserAsync(uid, ct)
            : null;

        var outcome = GateAdmissionRules.Evaluate(new GateScanContext(
            Found: true,
            IsVoid: attendee.Status == TicketAttendeeStatus.Void,
            AlreadyAdmittedLocally: priorAdmit is not null,
            CheckedInAtVendor: attendee.Status == TicketAttendeeStatus.CheckedIn,
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
            // Date-only context for a precise too-early reason on the card (never the EE source — privacy I2).
            EarliestEntryDate: ee?.EarliestEntryDate,
            Today: today,
            GeneralEntryDate: settings.IsCutoffConfigured ? settings.GeneralEntryOpensAt.InZone(zone).Date : null);
    }

    public async Task<GateDecisionResult> RecordDecisionAsync(
        GateDecisionInput input, Guid scannedByUserId, CancellationToken ct = default)
    {
        // Re-evaluate authoritatively: a client-supplied "ID confirmed" can never
        // turn a STOP into an admit.
        var eval = await EvaluateAsync(input.Barcode, ct);
        var verdict = ResolveVerdict(eval.Outcome, input);
        var admit = IsAdmit(verdict);

        var recorded = await repository.RecordScanAsync(BuildEvent(eval, input, scannedByUserId, verdict, admit), ct);

        if (recorded == GateRecordOutcome.DuplicateAdmitRejected)
        {
            // Lost the concurrent race for this barcode's single admit slot:
            // record the attempt as a duplicate and report it as such.
            verdict = GateVerdict.RejectedDuplicate;
            await repository.RecordScanAsync(BuildEvent(eval, input, scannedByUserId, verdict, admit: false), ct);
            var prior = await repository.GetAdmitForBarcodeAsync(eval.Barcode, ct);
            return new GateDecisionResult(verdict, eval.GuestName, eval.TicketTypeName,
                IsEarly: false, prior?.OccurredAt, prior?.ScannedByUserId);
        }

        return new GateDecisionResult(verdict, eval.GuestName, eval.TicketTypeName,
            IsEarly: admit && eval.IsEarly,
            eval.PreviousAdmitAt, eval.PreviousAdmitByUserId,
            // Only surface the vendor ticket id when there's an admit to mirror.
            VendorTicketId: admit ? eval.VendorTicketId : null);
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

    public async Task<IReadOnlyList<GateRosterEntry>> GetShiftRosterAsync(
        Guid rosterTeamId, CancellationToken ct = default)
    {
        var activeEvent = await shifts.GetActiveAsync();
        if (activeEvent is null)
            return [];

        // Shift dates are stored as DayOffset from the event's GateOpeningDate; resolve each
        // shift's start to an instant in the event zone and keep only those starting near now,
        // so the claim screen shows the crew on around shift change, not the whole event roster.
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        var now = clock.GetCurrentInstant();
        var openingDate = activeEvent.GateOpeningDate;

        var gateShifts = await shifts.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            activeEvent.Id, DepartmentId: rosterTeamId, Flags: ShiftBrowseQueryFlags.IncludeSignups));

        return gateShifts
            .Where(s => StartsWithinWindow(openingDate, s.Shift, zone, now))
            // "Signed up" = not a negative terminal state; one person may hold several shifts,
            // so de-dupe to one pick per human.
            .SelectMany(s => s.Signups)
            .Where(u => u.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .DistinctBy(u => u.UserId)
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new GateRosterEntry(u.UserId, u.DisplayName))
            .ToList();
    }

    private static bool StartsWithinWindow(LocalDate openingDate, Shift shift, DateTimeZone zone, Instant now)
    {
        var start = openingDate.PlusDays(shift.DayOffset).At(shift.StartTime)
            .InZoneLeniently(zone).ToInstant();
        return start >= now - RosterWindow && start <= now + RosterWindow;
    }

    // ── Personal device PINs ─────────────────────────────────────────────────
    public async Task<GatePinStatus> GetPinStatusAsync(Guid userId, CancellationToken ct = default) =>
        new(await repository.GetStaffPinAsync(userId, ct) is not null, await IsSupervisorAsync(userId, ct));

    public async Task<GatePinSetResult> SetOwnPinAsync(Guid userId, string pin, CancellationToken ct = default)
    {
        if (!IsValidPin(pin))
            return GatePinSetResult.InvalidPin;
        // A supervisor's PIN carries override authority — never let it be minted from the
        // anonymous kiosk (an attacker could cold-enrol a supervisor and impersonate them).
        if (await IsSupervisorAsync(userId, ct))
            return GatePinSetResult.SupervisorMustBeAdminEnrolled;
        await StorePinAsync(userId, pin, ct);
        // Self-enrol at the kiosk: actor == the claimed staffer (no PIN value is ever logged).
        await auditLog.LogAsync(AuditAction.GateStaffPinSet, nameof(GateStaffPin), userId,
            "Staffer self-enrolled their gate PIN at the kiosk", userId);
        return GatePinSetResult.Ok;
    }

    public async Task<bool> AdminSetPinAsync(Guid userId, string pin, Guid actorUserId, CancellationToken ct = default)
    {
        if (!IsValidPin(pin))
            return false;
        await StorePinAsync(userId, pin, ct);
        await auditLog.LogAsync(AuditAction.GateStaffPinSet, nameof(GateStaffPin), userId,
            "Admin set a staffer's gate PIN", actorUserId);
        return true;
    }

    public async Task<bool> VerifyPinAsync(Guid userId, string pin, CancellationToken ct = default)
    {
        var row = await repository.GetStaffPinAsync(userId, ct);
        if (row is null)
            return false;
        // PasswordHasher does a fixed-time compare internally.
        return pinHasher.VerifyHashedPassword(row, row.PinHash, pin) != PasswordVerificationResult.Failed;
    }

    public async Task<bool> AuthorizeOverrideAsync(Guid supervisorUserId, string pin, CancellationToken ct = default)
    {
        // Verify-only: enrolled + correct PIN + currently holds a supervisor role. Never enrol here.
        if (!await VerifyPinAsync(supervisorUserId, pin, ct))
            return false;
        return await IsSupervisorAsync(supervisorUserId, ct);
    }

    public async Task ClearPinAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        await repository.DeleteStaffPinAsync(userId, ct);
        await auditLog.LogAsync(AuditAction.GateStaffPinReset, nameof(GateStaffPin), userId,
            "Admin reset a staffer's gate PIN", actorUserId);
    }

    public async Task<IReadOnlyList<Guid>> GetEnrolledSupervisorIdsAsync(CancellationToken ct = default)
    {
        // Union the supervisor-role holders, then keep only those with a PIN — an un-enrolled
        // supervisor can't override anyway, so listing them would just offer dead picks.
        var supervisors = new HashSet<Guid>();
        foreach (var role in SupervisorRoles)
            foreach (var id in await roles.GetActiveUserIdsInRoleAsync(role, ct))
                supervisors.Add(id);

        var enrolled = new List<Guid>();
        foreach (var id in supervisors)
            if (await repository.GetStaffPinAsync(id, ct) is not null)
                enrolled.Add(id);
        return enrolled;
    }

    private async Task<bool> IsSupervisorAsync(Guid userId, CancellationToken ct)
    {
        foreach (var role in SupervisorRoles)
            if (await roles.HasActiveRoleAsync(userId, role, ct))
                return true;
        return false;
    }

    private async Task StorePinAsync(Guid userId, string pin, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var entity = new GateStaffPin { UserId = userId, CreatedAt = now, UpdatedAt = now };
        entity.PinHash = pinHasher.HashPassword(entity, pin);
        await repository.UpsertStaffPinAsync(entity, ct);
    }

    /// <summary>Exactly four digits, and not a trivially-guessable run/repeat (0000, 1234, 4321…).</summary>
    private static bool IsValidPin(string? pin)
    {
        if (pin is null || pin.Length != 4 || !pin.All(char.IsAsciiDigit))
            return false;
        if (pin.Distinct().Count() == 1)
            return false;
        return !"0123456789".Contains(pin, StringComparison.Ordinal)
            && !"9876543210".Contains(pin, StringComparison.Ordinal);
    }

    // ── IUserMerge ───────────────────────────────────────────────────────────
    // actorUserId/now are intentionally unused: gate_scan_events carries no
    // UpdatedAt/audit column (it's attribution-by-id only), and the merge itself
    // is audited by AccountMergeService. Per-row merge provenance would be a
    // schema change, not a code change. Cache eviction is the orchestrator's job
    // (Gate has no cache).
    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignUserAsync(mergedFromUserId, mergedToUserId, ct);

    // ── IUserDataContributor (GDPR Article 15) ───────────────────────────────
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var scans = await repository.GetScansForUserAsync(userId, ct);

        // Data-minimized: a person's own gate activity (as guest or as scanner),
        // without the barcode or any other person's identifiers.
        var slice = new UserDataSlice(GdprExportSections.GateScans, scans.Select(s => new
        {
            OccurredAt = s.OccurredAt.ToIso8601(),
            Verdict = s.Verdict.ToString(),
            Role = s.GuestUserId == userId ? "Guest" : "Scanner",
            s.LaneId,
        }).ToList());

        return [slice];
    }

    private static GateScanResult NotFound(string code) =>
        new(GatePreCheckOutcome.Invalid, code, null, null, false, null, null, null, null, null);

    private static GateVerdict ResolveVerdict(GatePreCheckOutcome outcome, GateDecisionInput input) => outcome switch
    {
        GatePreCheckOutcome.Invalid => GateVerdict.RejectedInvalid,
        GatePreCheckOutcome.Duplicate => GateVerdict.RejectedDuplicate,
        GatePreCheckOutcome.CutoffNotConfigured => GateVerdict.Unresolved,
        GatePreCheckOutcome.EarlyEntryUnknown => GateVerdict.Unresolved,
        // A too-early scan is admissible only by a supervisor override; the controller sets
        // OverrideByUserId solely after AuthorizeOverrideAsync, so a non-null value is proof.
        GatePreCheckOutcome.TooEarly =>
            input.OverrideByUserId is not null ? GateVerdict.AdmittedEarlyOverride : GateVerdict.RejectedTooEarly,
        GatePreCheckOutcome.NeedsIdCheck or GatePreCheckOutcome.NeedsIdCheckEarly =>
            // The child-without-ID waiver is itself a supervisor override now (per-PIN), so it
            // only admits when an authorizing supervisor was recorded.
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
            // The audit trail for "who let this person in early / without ID" — only recorded on an admit verdict.
            OverrideByUserId = admit ? input.OverrideByUserId : null,
            AdmitDedupeKey = admit ? eval.Barcode : null,
        };

    private static bool IsAdmit(GateVerdict v) =>
        v is GateVerdict.Admitted or GateVerdict.AdmittedEarly
            or GateVerdict.AdmittedChildWithAdult or GateVerdict.AdmittedEarlyOverride;

    private static DateTimeZone EventZone(string? timeZoneId) =>
        (timeZoneId is not null ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) : null) ?? DateTimeZone.Utc;
}
