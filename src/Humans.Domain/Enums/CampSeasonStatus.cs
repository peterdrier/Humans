namespace Humans.Domain.Enums;

public enum CampSeasonStatus
{
    Pending = 0,
    Active = 1,
    Full = 2,
    // Inactive = 3 (removed — use Withdrawn instead)
    Rejected = 4,
    Withdrawn = 5
}
