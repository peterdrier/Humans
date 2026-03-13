namespace Humans.Domain.Entities;

public class BarrioSettings
{
    public Guid Id { get; init; }
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = new();
}
