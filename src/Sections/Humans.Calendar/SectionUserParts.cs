using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;

namespace Humans.Calendar;

/// <summary>
/// Contributes <see cref="UserCalendarViewComponent"/> to the admin human-detail page
/// (nobodies-collective/Humans#1815). Discovered by <c>RegisterContributions</c>
/// (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() => [new(UserPartSlots.AdminDetail, typeof(UserCalendarViewComponent))];
}
