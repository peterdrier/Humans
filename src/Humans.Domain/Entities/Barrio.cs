using NodaTime;

namespace Humans.Domain.Entities;

public class Barrio
{
    public Guid Id { get; init; }
    public string Slug { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string? WebOrSocialUrl { get; set; }
    public string ContactMethod { get; set; } = string.Empty;
    public bool IsSwissCamp { get; set; }
    public int TimesAtNowhere { get; set; }

    public Guid CreatedByUserId { get; init; }
    public User CreatedByUser { get; set; } = null!;

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<BarrioSeason> Seasons { get; set; } = new List<BarrioSeason>();
    public ICollection<BarrioLead> Leads { get; set; } = new List<BarrioLead>();
    public ICollection<BarrioHistoricalName> HistoricalNames { get; set; } = new List<BarrioHistoricalName>();
    public ICollection<BarrioImage> Images { get; set; } = new List<BarrioImage>();
}
