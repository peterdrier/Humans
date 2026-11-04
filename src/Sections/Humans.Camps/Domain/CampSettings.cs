namespace Humans.Camps.Domain;

internal sealed class CampSettings
{
    public Guid Id { get; init; }

    public List<int> OpenSeasons { get; set; } = [];
}
