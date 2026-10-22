
namespace Humans.Users.Contracts;

public interface IContactFieldService
{
    /// <summary>Filtered to what <paramref name="viewerUserId"/> may see. Served from the <c>UserInfo</c> cache.</summary>
    Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid userId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Every field, for the owner's editor.</summary>
    Task<IReadOnlyList<ContactFieldEditDto>> GetAllContactFieldsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts and deletes the current fields. Historical email fields stay unless listed in
    /// <paramref name="removedLegacyContactFieldIds"/>.
    /// </summary>
    Task SaveContactFieldsAsync(
        Guid profileId,
        IReadOnlyList<ContactFieldEditDto> fields,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? removedLegacyContactFieldIds = null);

    Task<ContactFieldVisibility> GetViewerAccessLevelAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);
}
