using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Camps.Domain;

internal sealed class CampHistoricalName
{
    public Guid Id { get; init; }

    public Guid CampId { get; init; }
    public Camp Camp { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public CampNameSource Source { get; init; }

    public Instant CreatedAt { get; init; }
}
