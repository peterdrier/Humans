using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class BarrioHistoricalName
{
    public Guid Id { get; init; }

    public Guid BarrioId { get; init; }
    public Barrio Barrio { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public BarrioNameSource Source { get; init; }

    public Instant CreatedAt { get; init; }
}
