using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;

namespace Humans.Calendar;

internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() => [new(UserPartSlots.AdminDetail, typeof(UserCalendarViewComponent))];
}
