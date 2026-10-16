namespace Humans.Camps.Contracts;

public enum CampSeasonStatus
{
    Pending = 0,
    Active = 1,
    Full = 2,
    // 3 reserved (retired Inactive); do not reuse
    Rejected = 4,
    Withdrawn = 5
}
