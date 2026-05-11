using NodaTime;

namespace Humans.Domain.Entities;

public class CampSettings
{
    public Guid Id { get; init; }
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = new();

    /// <summary>
    /// Date from which humans holding an EE grant for the current public year
    /// may enter the site. Set by CampAdmin; nullable until configured for the
    /// year. v1 has no consumer beyond informational display on /Camps/Admin —
    /// a future gate endpoint will read this.
    /// </summary>
    public LocalDate? EeStartDate { get; set; }
}
