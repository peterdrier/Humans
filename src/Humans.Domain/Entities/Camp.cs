using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Domain.Entities;

public class Camp
{
    public Guid Id { get; init; }
    public string Slug { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string? WebOrSocialUrl { get; set; }
    public List<CampLink>? Links { get; set; }
    public bool IsSwissCamp { get; set; }
    public int TimesAtNowhere { get; set; }

    public Guid CreatedByUserId { get; init; }
    public User CreatedByUser { get; set; } = null!;

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<CampSeason> Seasons { get; set; } = new List<CampSeason>();
    public ICollection<CampLead> Leads { get; set; } = new List<CampLead>();
    public ICollection<CampHistoricalName> HistoricalNames { get; set; } = new List<CampHistoricalName>();
    public ICollection<CampImage> Images { get; set; } = new List<CampImage>();
}
