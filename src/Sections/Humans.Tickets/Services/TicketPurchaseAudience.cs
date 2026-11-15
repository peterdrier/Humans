using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Tickets.Services;

internal sealed class TicketPurchaseAudience(
    ITicketRepository ticketRepository,
    IUserService userService,
    IUserEmailService userEmailService,
    ITeamService teamService,
    IGovernanceService governanceService,
    ISettingsService settingsService,
    IEventParticipationManagement participations) : IApplicationService
{
    internal async Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize, CancellationToken ct = default)
    {
        var matchedUserIds = await GetCurrentEventTicketHolderIdsAsync(ct);

        var allUsers = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var volunteerTeam = await teamService.GetTeamAsync(SystemTeamIds.Volunteers, ct);
        var volunteerUserIds = volunteerTeam?.Members.Select(m => m.UserId).ToHashSet() ?? [];

        var candidateIds = allUsers
            .Where(u => volunteerUserIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToList();

        var activeEvent = settingsService.GetActiveEventSettings();
        HashSet<Guid> notAttendingSet = [];
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            notAttendingSet = (await participations.GetAllParticipationsAsync(ct))
                .Where(ep => ep.Year == activeEvent.Year && ep.Status == ParticipationStatus.NotAttending)
                .Select(ep => ep.UserId)
                .ToHashSet();
        }

        candidateIds = candidateIds.Where(id => !notAttendingSet.Contains(id)).ToList();
        if (candidateIds.Count == 0)
        {
            return new WhoHasntBoughtResult
            {
                Humans = [],
                TotalCount = 0,
                AvailableTeams = [],
            };
        }

        var emailsById = await userEmailService.GetNotificationEmailsByUserIdsAsync(candidateIds, ct);
        var candidateIdSet = candidateIds.ToHashSet();
        var teamsByIdLookup = await teamService.GetTeamsAsync(ct);
        var teamsByUser = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var team in teamsByIdLookup.Values
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
        {
            foreach (var member in team.Members.Where(m => candidateIdSet.Contains(m.UserId)))
            {
                if (!teamsByUser.TryGetValue(member.UserId, out var names))
                {
                    names = new List<string>();
                    teamsByUser[member.UserId] = names;
                }
                ((List<string>)names).Add(team.Name);
            }
        }

        var usersById = allUsers.ToDictionary(u => u.Id);
        var tiers = await governanceService.GetMembershipTiersAsync(ct);

        var rows = candidateIds
            .Where(id => usersById.TryGetValue(id, out var u) && u.Profile is not null)
            .Select(id =>
            {
                var user = usersById[id];
                emailsById.TryGetValue(id, out var email);
                teamsByUser.TryGetValue(id, out var teamNames);

                return new WhoHasntBoughtRow
                {
                    UserId = id,
                    HasTicket = matchedUserIds.Contains(id),
                    Name = user.BurnerName,
                    Email = email ?? string.Empty,
                    TeamNames = teamNames ?? [],
                    Tier = tiers.GetValueOrDefault(id, MembershipTier.Volunteer),
                };
            })
            .ToList();

        // Search all verified emails so admins can find secondary-address purchases.
        HashSet<Guid>? emailMatchUserIds = null;
        if (search.HasSearchTerm(1))
        {
            var term = search.Trim();
            emailMatchUserIds = allUsers
                .Where(u => u.UserEmails.Any(e => e.IsVerified && e.Email.ContainsOrdinalIgnoreCase(term)))
                .Select(u => u.Id)
                .ToHashSet();
        }

        var filtered = FilterWhoHasntBoughtRows(
            rows, filterTicketStatus, filterTeam, filterTier, search, emailMatchUserIds);

        var totalCount = filtered.Count;
        var pagedHumans = filtered
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize)
            .Select(r => new WhoHasntBoughtRowDto
            {
                UserId = r.UserId,
                HasTicket = r.HasTicket,
                Email = r.Email,
                Teams = string.Join(", ", r.TeamNames),
                Tier = r.Tier,
            })
            .ToList();

        var availableTeams = rows
            .SelectMany(r => r.TeamNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WhoHasntBoughtResult
        {
            Humans = pagedHumans,
            TotalCount = totalCount,
            AvailableTeams = availableTeams,
        };
    }

    private sealed class WhoHasntBoughtRow
    {
        public required Guid UserId { get; init; }
        public required bool HasTicket { get; init; }
        public required string Name { get; init; }
        public required string Email { get; init; }
        public required IReadOnlyList<string> TeamNames { get; init; }
        public required MembershipTier Tier { get; init; }
    }

    private static List<WhoHasntBoughtRow> FilterWhoHasntBoughtRows(
        IEnumerable<WhoHasntBoughtRow> rows,
        string? filterTicketStatus,
        string? filterTeam,
        string? filterTier,
        string? search,
        IReadOnlySet<Guid>? emailMatchUserIds = null)
    {
        IEnumerable<WhoHasntBoughtRow> filtered = rows;

        if (string.Equals(filterTicketStatus, "bought", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(r => r.HasTicket);
        else if (string.Equals(filterTicketStatus, "not_bought", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(r => !r.HasTicket);

        if (!string.IsNullOrEmpty(filterTeam))
            filtered = filtered.Where(r =>
                r.TeamNames.Any(tn => string.Equals(tn, filterTeam, StringComparison.OrdinalIgnoreCase)));

        if (!string.IsNullOrEmpty(filterTier) &&
            Enum.TryParse<MembershipTier>(filterTier, ignoreCase: true, out var parsedTier))
        {
            filtered = filtered.Where(r => r.Tier == parsedTier);
        }

        if (search.HasSearchTerm(1))
        {
            filtered = filtered.Where(r =>
                r.Name.ContainsOrdinalIgnoreCase(search) ||
                r.Email.ContainsOrdinalIgnoreCase(search) ||
                (emailMatchUserIds is not null && emailMatchUserIds.Contains(r.UserId)));
        }

        return filtered.ToList();
    }

    private async Task<HashSet<Guid>> GetCurrentEventTicketHolderIdsAsync(CancellationToken ct)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        if (syncState is null || string.IsNullOrEmpty(syncState.VendorEventId))
            return [];

        var ids = await ticketRepository.GetEventTicketHolderUserIdsAsync(syncState.VendorEventId, ct);
        return ids.ToHashSet();
    }
}
