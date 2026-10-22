namespace Humans.Camps.Contracts;

public interface ICampLeadDirectory
{
    /// <summary>Any camp, any season: the Barrio Leads team spans years.</summary>
    Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Any camp, any season.</summary>
    Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken cancellationToken = default);
}
