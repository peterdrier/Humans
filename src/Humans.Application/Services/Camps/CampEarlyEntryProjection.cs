using Humans.Application.Interfaces.EarlyEntry;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Camps;

/// <summary>
/// Pure projection from the camps' membership data to EE grants. The entry date
/// is the single global <c>CampSettings.EeStartDate</c>; only Active members with
/// <c>HasEarlyEntry</c> contribute. Source label is "Camp: {season name}".
/// </summary>
internal static class CampEarlyEntryProjection
{
    internal static IReadOnlyList<EarlyEntryGrant> Project(
        LocalDate eeStartDate,
        IReadOnlyDictionary<Guid, IReadOnlyList<CampMember>> membersBySeasonId,
        IReadOnlyDictionary<Guid, string> seasonNameById)
    {
        var grants = new List<EarlyEntryGrant>();
        foreach (var (seasonId, members) in membersBySeasonId)
        {
            var name = seasonNameById.GetValueOrDefault(seasonId, "Camp");
            foreach (var m in members)
            {
                if (m.Status != CampMemberStatus.Active || !m.HasEarlyEntry) continue;
                grants.Add(new EarlyEntryGrant(m.UserId, eeStartDate, $"Camp: {name}"));
            }
        }
        return grants;
    }
}
