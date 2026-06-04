using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Camps;

/// <summary>
/// Computes barrio shift-obligation compliance and owns the obligation config +
/// per-season overrides. See <see cref="IShiftObligationService"/>. Cross-section
/// reads go through <see cref="IShiftServiceRead"/> / <see cref="ITeamServiceRead"/>
/// / <see cref="ICampServiceRead"/> / <see cref="IUserService"/>; same-section
/// reads use <see cref="IShiftObligationRepository"/> and
/// <see cref="ICampRepository"/>. No EF entity leaves the section.
/// </summary>
public sealed class ShiftObligationService : IShiftObligationService
{
    private readonly IShiftObligationRepository obligationRepo;
    private readonly ICampServiceRead campServiceRead;
    private readonly ICampRepository campRepository;
    private readonly IShiftServiceRead shiftServiceRead;
    private readonly ITeamServiceRead teamServiceRead;
    private readonly IUserService userService;
    private readonly IClock clock;

    // Wired now; consumed by the reminder methods in Chunk 4.
    private readonly IEmailService emailService;
    private readonly IEmailMessageFactory emailMessageFactory;
    private readonly IAuditLogService auditLog;
    private readonly ILogger<ShiftObligationService> logger;

    public ShiftObligationService(
        IShiftObligationRepository obligationRepo,
        ICampServiceRead campServiceRead,
        ICampRepository campRepository,
        IShiftServiceRead shiftServiceRead,
        ITeamServiceRead teamServiceRead,
        IUserService userService,
        IEmailService emailService,
        IEmailMessageFactory emailMessageFactory,
        IAuditLogService auditLog,
        IClock clock,
        ILogger<ShiftObligationService> logger)
    {
        this.obligationRepo = obligationRepo;
        this.campServiceRead = campServiceRead;
        this.campRepository = campRepository;
        this.shiftServiceRead = shiftServiceRead;
        this.teamServiceRead = teamServiceRead;
        this.userService = userService;
        this.emailService = emailService;
        this.emailMessageFactory = emailMessageFactory;
        this.auditLog = auditLog;
        this.clock = clock;
        this.logger = logger;
    }

    public async Task<BarrioObligationMatrix> GetComplianceMatrixAsync(int year, CancellationToken ct = default)
    {
        var camps = await campServiceRead.GetCampsForYearAsync(year, ct);
        var seasons = ResolveActiveSeasons(camps, year);
        var functions = await GetActiveFunctionsOrderedAsync(ct);

        var overrideLookup = await BuildOverrideLookupAsync(seasons, ct);
        var countsByFunction = await BuildCountsByFunctionAsync(functions, ct);
        var columns = await BuildColumnsAsync(functions, ct);

        var exempt = new List<ExemptBarrio>();
        var offGrid = new List<OffGridBarrio>();
        var rows = new List<BarrioRow>();

        foreach (var season in seasons)
        {
            var activeMemberIds = ActiveMemberIds(season);
            var activeMemberCount = activeMemberIds.Count;

            // Layer 2a — global Nobodies-Org exemption: dropped from the matrix entirely.
            if (season.ElectricalGrid == ElectricalGrid.Norg)
            {
                exempt.Add(new ExemptBarrio(season.Id, season.Name, activeMemberCount));
                continue;
            }

            var cells = new List<ObligationCell>(functions.Count);
            foreach (var function in functions)
            {
                var applicable = IsApplicable(function, season, offGrid);

                var counts = countsByFunction[function.Id];
                var done = DoneFor(counts, activeMemberIds);
                var required = RequiredFor(overrideLookup, season.Id, function);
                var underMembered = applicable && activeMemberCount < required;

                cells.Add(new ObligationCell(function.Id, applicable, done, required, underMembered));
            }

            rows.Add(new BarrioRow(season.Id, season.Name, season.CampSlug, activeMemberCount, cells));
        }

        return new BarrioObligationMatrix(year, columns, rows, exempt, offGrid);
    }

    public async Task<BarrioObligationDetail?> GetBarrioObligationDetailAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        var season = await campRepository.GetSeasonByIdAsync(campSeasonId, ct);
        if (season is null)
        {
            return null;
        }

        var camps = await campServiceRead.GetCampsForYearAsync(season.Year, ct);
        // Same season-status gate as the matrix (ResolveActiveSeasons): a Pending/Rejected
        // season is not part of the matrix, so it has no detail to render either.
        var seasonInfo = ResolveActiveSeasons(camps, season.Year)
            .FirstOrDefault(s => s.Id == campSeasonId);
        if (seasonInfo is null)
        {
            return null;
        }

        // Norg barrios are exempt from the matrix; the detail page mirrors that.
        if (seasonInfo.ElectricalGrid == ElectricalGrid.Norg)
        {
            return new BarrioObligationDetail(campSeasonId, seasonInfo.Name, []);
        }

        var functions = await GetActiveFunctionsOrderedAsync(ct);
        var overrideLookup = await BuildOverrideLookupAsync([seasonInfo], ct);
        var countsByFunction = await BuildCountsByFunctionAsync(functions, ct);

        var activeMemberIds = ActiveMemberIds(seasonInfo);
        var names = await ResolveDisplayNamesAsync(activeMemberIds, ct);

        var functionRows = new List<ObligationDetailFunction>();
        foreach (var function in functions)
        {
            if (!IsApplicable(function, seasonInfo, offGridSink: null))
            {
                continue;
            }

            var counts = countsByFunction[function.Id];
            var signedUp = activeMemberIds
                .Select(id => new SignedUpMember(id, NameFor(names, id), counts.TryGetValue(id, out var n) ? n : 0))
                .Where(m => m.Count > 0)
                .OrderByDescending(m => m.Count)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var notYet = activeMemberIds
                .Where(id => !counts.TryGetValue(id, out var n) || n == 0)
                .Select(id => NameFor(names, id))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var done = signedUp.Sum(m => m.Count);
            var required = RequiredFor(overrideLookup, campSeasonId, function);

            functionRows.Add(new ObligationDetailFunction(
                function.Id, function.CampRoleSlug, done, required, signedUp, notYet));
        }

        return new BarrioObligationDetail(campSeasonId, seasonInfo.Name, functionRows);
    }

    public async Task<IReadOnlyList<ShiftObligationConfigInfo>> GetFunctionsAsync(CancellationToken ct = default)
    {
        var functions = await obligationRepo.GetAllAsync(ct);
        var result = new List<ShiftObligationConfigInfo>(functions.Count);
        foreach (var f in functions)
        {
            var targetName = await ResolveTargetNameAsync(f.TargetType, f.TargetId, ct);
            result.Add(new ShiftObligationConfigInfo(
                f.Id, f.TargetType, f.TargetId, targetName, f.CampRoleSlug,
                f.Applicability, f.DefaultRequiredShiftCount, f.IsActive, f.SortOrder));
        }

        return result;
    }

    public async Task UpsertFunctionAsync(
        ShiftObligationConfigInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredCount = Math.Max(0, input.DefaultRequiredShiftCount);

        if (input.Id is { } id)
        {
            var existing = await obligationRepo.GetByIdAsync(id, ct);
            if (existing is null)
            {
                throw new InvalidOperationException($"Shift obligation '{id}' not found.");
            }

            existing.TargetType = input.TargetType;
            existing.TargetId = input.TargetId;
            existing.CampRoleSlug = input.CampRoleSlug;
            existing.Applicability = input.Applicability;
            existing.DefaultRequiredShiftCount = requiredCount;
            existing.IsActive = input.IsActive;
            existing.SortOrder = input.SortOrder;
            existing.UpdatedAt = now;
            await obligationRepo.UpdateAsync(existing, ct);
            await auditLog.LogAsync(
                AuditAction.BarrioShiftObligationConfigChanged, "ShiftObligation", existing.Id,
                $"Updated shift-obligation function '{existing.CampRoleSlug}'.", actorUserId);
            return;
        }

        var created = new ShiftObligation
        {
            Id = Guid.NewGuid(),
            TargetType = input.TargetType,
            TargetId = input.TargetId,
            CampRoleSlug = input.CampRoleSlug,
            Applicability = input.Applicability,
            DefaultRequiredShiftCount = requiredCount,
            IsActive = input.IsActive,
            SortOrder = input.SortOrder,
            CreatedAt = now,
        };
        await obligationRepo.AddAsync(created, ct);
        await auditLog.LogAsync(
            AuditAction.BarrioShiftObligationConfigChanged, "ShiftObligation", created.Id,
            $"Created shift-obligation function '{created.CampRoleSlug}'.", actorUserId);
    }

    public async Task SetOverrideAsync(
        Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount,
        Guid actorUserId, CancellationToken ct = default)
    {
        // Clamp negatives to 0; null clears the override.
        var clamped = requiredShiftCount is { } n ? Math.Max(0, n) : (int?)null;
        await obligationRepo.SetOverrideAsync(campSeasonId, shiftObligationId, clamped, ct);
        var change = clamped is { } v
            ? $"Set required-shift override to {v}."
            : "Cleared required-shift override.";
        await auditLog.LogAsync(
            AuditAction.BarrioShiftObligationConfigChanged, "ShiftObligation", shiftObligationId,
            change, actorUserId, relatedEntityId: campSeasonId, relatedEntityType: "CampSeason");
    }

    public async Task SendReminderAsync(
        Guid campSeasonId, Guid shiftObligationId, Guid actorUserId, CancellationToken ct = default)
    {
        var function = await obligationRepo.GetByIdAsync(shiftObligationId, ct);
        if (function is null)
        {
            return;
        }

        var season = await ResolveActiveSeasonAsync(campSeasonId, ct);
        if (season is null)
        {
            return;
        }

        await SendReminderForBarrioAsync(function, season, actorUserId, ct);
    }

    public async Task<int> RemindAllNonCompliantAsync(
        Guid shiftObligationId, Guid actorUserId, CancellationToken ct = default)
    {
        var function = await obligationRepo.GetByIdAsync(shiftObligationId, ct);
        if (function is null)
        {
            return 0;
        }

        // The function is year-agnostic; reminders fire for the current public
        // matrix year. Resolve the active seasons the same way the matrix does,
        // then keep only the applicable + not-met barrios.
        var emailed = 0;
        var seasons = await ResolveCurrentYearActiveSeasonsAsync(ct);
        var counts = await CountsForFunctionAsync(function, ct);
        var overrideLookup = await BuildOverrideLookupAsync(seasons, ct);

        foreach (var season in seasons)
        {
            // Norg barrios are globally exempt — never reminded.
            if (season.ElectricalGrid == ElectricalGrid.Norg)
            {
                continue;
            }

            if (!IsApplicable(function, season, offGridSink: null))
            {
                continue;
            }

            var activeMemberIds = ActiveMemberIds(season);
            var done = DoneFor(counts, activeMemberIds);
            var required = RequiredFor(overrideLookup, season.Id, function);
            if (done >= required)
            {
                continue;
            }

            if (await SendReminderForBarrioAsync(function, season, actorUserId, ct))
            {
                emailed++;
            }
        }

        return emailed;
    }

    // ----- internals --------------------------------------------------------

    private static IReadOnlyList<CampSeasonInfo> ResolveActiveSeasons(
        IReadOnlyList<CampInfo> camps, int year) =>
        camps
            .SelectMany(c => c.Seasons)
            .Where(s => s.Year == year && s.Status is CampSeasonStatus.Active or CampSeasonStatus.Full)
            .ToList();

    private async Task<IReadOnlyList<ShiftObligation>> GetActiveFunctionsOrderedAsync(CancellationToken ct)
    {
        var all = await obligationRepo.GetAllAsync(ct);
        return all
            .Where(f => f.IsActive)
            .OrderBy(f => f.SortOrder)
            .ToList();
    }

    private async Task<IReadOnlyDictionary<(Guid SeasonId, Guid FunctionId), int>> BuildOverrideLookupAsync(
        IReadOnlyCollection<CampSeasonInfo> seasons, CancellationToken ct)
    {
        var seasonIds = seasons.Select(s => s.Id).Distinct().ToList();
        var overrides = await obligationRepo.GetOverridesForSeasonsAsync(seasonIds, ct);
        return overrides.ToDictionary(
            o => (o.CampSeasonId, o.ShiftObligationId),
            o => o.RequiredShiftCount);
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, int>>> BuildCountsByFunctionAsync(
        IReadOnlyList<ShiftObligation> functions, CancellationToken ct)
    {
        var map = new Dictionary<Guid, IReadOnlyDictionary<Guid, int>>();
        foreach (var function in functions)
        {
            map[function.Id] = await CountsForFunctionAsync(function, ct);
        }

        return map;
    }

    private async Task<IReadOnlyDictionary<Guid, int>> CountsForFunctionAsync(
        ShiftObligation function, CancellationToken ct) =>
        function.TargetType switch
        {
            ShiftObligationTargetType.Team =>
                await shiftServiceRead.GetConfirmedSignupCountsByUserForTeamAsync(function.TargetId, ct),
            ShiftObligationTargetType.Rota =>
                await shiftServiceRead.GetConfirmedSignupCountsByUserForRotaAsync(function.TargetId, ct),
            _ => new Dictionary<Guid, int>(),
        };

    private static int RequiredFor(
        IReadOnlyDictionary<(Guid SeasonId, Guid FunctionId), int> overrideLookup,
        Guid seasonId, ShiftObligation function) =>
        overrideLookup.TryGetValue((seasonId, function.Id), out var ov)
            ? ov
            : function.DefaultRequiredShiftCount;

    private static int DoneFor(
        IReadOnlyDictionary<Guid, int> counts, IReadOnlyList<Guid> activeMemberIds) =>
        activeMemberIds.Sum(id => counts.TryGetValue(id, out var n) ? n : 0);

    private async Task<IReadOnlyList<ObligationColumn>> BuildColumnsAsync(
        IReadOnlyList<ShiftObligation> functions, CancellationToken ct)
    {
        var columns = new List<ObligationColumn>(functions.Count);
        foreach (var function in functions)
        {
            var (name, url) = await ResolveColumnTargetAsync(function, ct);
            columns.Add(new ObligationColumn(function.Id, name, url, function.Applicability));
        }

        return columns;
    }

    private async Task<(string Name, string Url)> ResolveColumnTargetAsync(
        ShiftObligation function, CancellationToken ct)
    {
        switch (function.TargetType)
        {
            case ShiftObligationTargetType.Team:
            {
                var team = await teamServiceRead.GetTeamAsync(function.TargetId, ct);
                var name = team?.Name ?? function.CampRoleSlug;
                // Team shifts live at /Teams/{slug}/Shifts (ShiftAdminController route prefix).
                var url = team is null ? string.Empty : $"/Teams/{team.Slug}/Shifts";
                return (name, url);
            }
            case ShiftObligationTargetType.Rota:
            {
                var rota = await shiftServiceRead.GetRotaTargetInfoAsync(function.TargetId, ct);
                var name = rota?.RotaName ?? function.CampRoleSlug;
                // Rotas live under their owning team's shifts page (no standalone
                // public rota route); anchor to the rota.
                var url = rota is null
                    ? string.Empty
                    : $"/Teams/{rota.TeamSlug}/Shifts#rota-{rota.RotaId}";
                return (name, url);
            }
            default:
                return (function.CampRoleSlug, string.Empty);
        }
    }

    private async Task<string> ResolveTargetNameAsync(
        ShiftObligationTargetType targetType, Guid targetId, CancellationToken ct)
    {
        return targetType switch
        {
            ShiftObligationTargetType.Team =>
                (await teamServiceRead.GetTeamAsync(targetId, ct))?.Name ?? string.Empty,
            ShiftObligationTargetType.Rota =>
                (await shiftServiceRead.GetRotaTargetInfoAsync(targetId, ct))?.RotaName ?? string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Per-function applicability (Layer 2b). For ElectricalGridConnected
    /// functions a barrio is non-applicable when its grid is OwnSupply, Unknown,
    /// or unset — those are collected into the off-grid list (once per barrio,
    /// keyed by season id) when <paramref name="offGridSink"/> is provided.
    /// AllBarrios functions are always applicable for non-exempt barrios.
    /// </summary>
    private static bool IsApplicable(
        ShiftObligation function, CampSeasonInfo season, List<OffGridBarrio>? offGridSink)
    {
        if (function.Applicability != ObligationApplicability.ElectricalGridConnected)
        {
            return true;
        }

        var grid = season.ElectricalGrid;
        // Exclusion-based on purpose: any *real* grid value counts as connected, so a future
        // grid colour (e.g. Orange) is included automatically without touching this code.
        // Norg is already removed in Layer A (global exemption).
        var connected = grid is not (null or ElectricalGrid.OwnSupply or ElectricalGrid.Unknown);
        if (connected)
        {
            return true;
        }

        if (offGridSink is not null && offGridSink.All(o => o.CampSeasonId != season.Id))
        {
            var reason = grid == ElectricalGrid.OwnSupply ? "OwnSupply" : "Unclassified";
            offGridSink.Add(new OffGridBarrio(season.Id, season.Name, reason));
        }

        return false;
    }

    private static IReadOnlyList<Guid> ActiveMemberIds(CampSeasonInfo season) =>
        season.Members
            .Where(m => m.Status == CampMemberStatus.Active)
            .Select(m => m.UserId)
            .Distinct()
            .ToList();

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var infos = await userService.GetUserInfosAsync(userIds, ct);
        return infos.ToDictionary(kv => kv.Key, kv => kv.Value.BurnerName);
    }

    private static string NameFor(IReadOnlyDictionary<Guid, string> names, Guid id) =>
        names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id.ToString();

    // ----- reminder internals -----------------------------------------------

    /// <summary>
    /// Resolves a single active/full season by id using the same season-status
    /// gate the matrix applies (<see cref="ResolveActiveSeasons"/>). Returns null
    /// for an unknown, Pending, or Rejected season.
    /// </summary>
    private async Task<CampSeasonInfo?> ResolveActiveSeasonAsync(Guid campSeasonId, CancellationToken ct)
    {
        var season = await campRepository.GetSeasonByIdAsync(campSeasonId, ct);
        if (season is null)
        {
            return null;
        }

        var camps = await campServiceRead.GetCampsForYearAsync(season.Year, ct);
        return ResolveActiveSeasons(camps, season.Year)
            .FirstOrDefault(s => s.Id == campSeasonId);
    }

    /// <summary>
    /// Active/full seasons for the current public matrix year — the year the
    /// bulk reminder operates on.
    /// </summary>
    private async Task<IReadOnlyList<CampSeasonInfo>> ResolveCurrentYearActiveSeasonsAsync(CancellationToken ct)
    {
        var settings = await campServiceRead.GetSettingsAsync(ct);
        var camps = await campServiceRead.GetCampsForYearAsync(settings.PublicYear, ct);
        return ResolveActiveSeasons(camps, settings.PublicYear);
    }

    /// <summary>
    /// Sends the reminder for one (barrio, function): recipients are the season
    /// leads unioned with the function's role-holders (by <c>CampRoleSlug</c>),
    /// de-duplicated. One email per distinct resolvable recipient, then one
    /// <see cref="AuditAction.BarrioShiftReminderSent"/> entry. Returns true when
    /// at least one email was sent (so the bulk caller can count emailed barrios).
    /// Zero recipients sends nothing and returns false.
    /// </summary>
    private async Task<bool> SendReminderForBarrioAsync(
        ShiftObligation function, CampSeasonInfo season, Guid actorUserId, CancellationToken ct)
    {
        var recipientIds = await ResolveRecipientIdsAsync(function, season, ct);
        if (recipientIds.Count == 0)
        {
            return false;
        }

        var counts = await CountsForFunctionAsync(function, ct);
        var overrideLookup = await BuildOverrideLookupAsync([season], ct);
        var activeMemberIds = ActiveMemberIds(season);
        var done = DoneFor(counts, activeMemberIds);
        var required = RequiredFor(overrideLookup, season.Id, function);

        // Reuse the matrix's column target so the link + function name never diverge.
        var (functionName, link) = await ResolveColumnTargetAsync(function, ct);

        var infos = await userService.GetUserInfosAsync(recipientIds, ct);
        var sent = 0;
        foreach (var id in recipientIds)
        {
            if (!infos.TryGetValue(id, out var info) || string.IsNullOrWhiteSpace(info.Email))
            {
                continue;
            }

            var message = emailMessageFactory.BarrioShiftObligationReminder(
                info.Email, info.BurnerName, season.Name, functionName,
                done, required, link, info.PreferredLanguage);
            await emailService.SendAsync(message, ct);
            sent++;
        }

        if (sent == 0)
        {
            return false;
        }

        await auditLog.LogAsync(
            AuditAction.BarrioShiftReminderSent, "ShiftObligation", function.Id,
            $"Sent {sent} shift-obligation reminder(s) for '{function.CampRoleSlug}' to barrio '{season.Name}'.",
            actorUserId, relatedEntityId: season.Id, relatedEntityType: "CampSeason");
        return true;
    }

    /// <summary>
    /// Recipients for one (barrio, function): the season's lead UserIds unioned
    /// with the active role-holders of the function's <c>CampRoleSlug</c> for the
    /// season's year, de-duplicated.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveRecipientIdsAsync(
        ShiftObligation function, CampSeasonInfo season, CancellationToken ct)
    {
        var holdersBySeason = await campRepository.GetRoleHolderUserIdsBySlugForYearAsync(
            function.CampRoleSlug, season.Year, ct);

        var recipients = new HashSet<Guid>(season.LeadUserIds);
        if (holdersBySeason.TryGetValue(season.Id, out var holders))
        {
            foreach (var holder in holders)
            {
                recipients.Add(holder);
            }
        }

        return recipients.ToList();
    }
}
