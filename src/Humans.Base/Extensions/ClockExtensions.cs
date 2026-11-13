using NodaTime;

namespace Humans.Base.Extensions;

public static class ClockExtensions
{
    public static LocalDate TodayIn(this IClock clock, DateTimeZone? zone) =>
        clock.GetCurrentInstant().InZone(zone ?? DateTimeZone.Utc).Date;
}
