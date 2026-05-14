using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Helpers;

/// <summary>
/// Cross-controller lookup helpers for the Events section. Both methods are
/// thin loops over already-cached read-models (UserInfo + CampLookup), so
/// the per-id awaits are dictionary hits in the steady state.
/// </summary>
public static class EventsLookupHelpers
{
    public static async Task<Dictionary<Guid, UserInfo>> LoadSubmittersAsync(
        IUserService users, IEnumerable<Guid> userIds)
    {
        var result = new Dictionary<Guid, UserInfo>();
        foreach (var id in userIds)
        {
            var info = await users.GetUserInfoAsync(id);
            if (info != null) result[id] = info;
        }
        return result;
    }

    public static async Task<Dictionary<Guid, CampInfo>> LoadCampsByIdAsync(
        ICampService camps, int? year)
    {
        if (year is null) return [];
        var list = await camps.GetCampsForYearAsync(year.Value);
        return list.ToDictionary(c => c.Id);
    }
}
