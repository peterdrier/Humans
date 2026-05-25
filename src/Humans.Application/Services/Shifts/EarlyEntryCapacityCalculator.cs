using Humans.Domain.Entities;

namespace Humans.Application.Services.Shifts;

internal static class EarlyEntryCapacityCalculator
{
    internal static int GetAvailableEeSlots(EventSettings settings, int dayOffset)
    {
        var totalCapacity = settings.GetEarlyEntryCapacityForDay(dayOffset);
        if (totalCapacity == 0) return 0;

        var barriosAllocation = 0;
        if (settings.BarriosEarlyEntryAllocation is not null)
        {
            var applicableKey = int.MinValue;
            foreach (var key in settings.BarriosEarlyEntryAllocation.Keys)
            {
                if (key <= dayOffset && key > applicableKey)
                    applicableKey = key;
            }
            if (applicableKey != int.MinValue)
                barriosAllocation = settings.BarriosEarlyEntryAllocation[applicableKey];
        }

        return Math.Max(0, totalCapacity - barriosAllocation);
    }
}
