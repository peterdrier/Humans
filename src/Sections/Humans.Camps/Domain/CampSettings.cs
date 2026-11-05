namespace Humans.Camps.Domain;

internal sealed class CampSettings
{
    public Guid Id { get; init; }

    /// <summary>Legacy mapped column; registration is owned by CampEventSettings. Kept for a separate schema drop.</summary>
    public List<int> OpenSeasons { get; set; } = [];
}
